// DampedLeastSquares.cs — Levenberg-Marquardt over the engine's residuals and Jacobian.
//
// THIS IS THE ONLY STAGE THAT IS ALLOWED TO BE C#, and the ledger is explicit about why: X1.4
// binds to "residuals + J". An optimiser consumes derivatives; it does not compute them. Every
// number that describes the DESIGN — the residual vector, the Jacobian, the merit — comes from the
// engine through Design. What is computed here is the step, which is linear algebra over numbers
// the engine produced and is not physics in any sense the no-physics rule was written to prevent.
//
// THE NORMAL EQUATIONS, AND WHY NOT QR. The step solves
//
//     (J^T J + lambda D) delta = -J^T r
//
// with D = diag(J^T J), the Marquardt scaling rather than a plain identity, so that the damping is
// in the units of each variable instead of assuming they are comparable. They are not: a design
// mixes a 0.4 nm layer with a 400 nm one, and an identity damping would move both by the same
// absolute amount and call it fair.
//
// Forming J^T J squares the condition number, which is the standard objection and the reason QR or
// SVD is usually preferred. It is the right trade here: n_variables is the LAYER COUNT, rarely
// past a hundred, so J^T J is small and cheap, while J itself has one row per residual and can
// have thousands. Damping also fixes what the squaring costs — lambda is raised until the matrix
// factors, which is exactly the regularisation an ill-conditioned normal system needs.
//
// A FAILED CHOLESKY IS INFORMATION, NOT AN ERROR. J^T J + lambda D is positive definite for
// lambda large enough, so a factorisation that fails means the damping is too small for the local
// curvature. Raising lambda and retrying is the algorithm working, not recovering.

using ThinFilm.Native;

namespace ThinFilm.Optimize;

public sealed class DlsOptions
{
    public int MaxIterations { get; init; } = 200;

    /// <summary>Starting damping. Small trusts the Gauss-Newton step; large starts near steepest descent.</summary>
    public double InitialDamping { get; init; } = 1e-3;

    public double DampingUp { get; init; } = 10.0;
    public double DampingDown { get; init; } = 0.1;

    /// <summary>Stop when the merit improves by less than this fraction of itself.</summary>
    public double MeritTolerance { get; init; } = 1e-12;

    /// <summary>Stop when every variable moves less than this, in the variable's own units (nm).</summary>
    public double StepTolerance { get; init; } = 1e-12;

    /// <summary>
    /// Stop when the gradient has fallen to this fraction of its starting value.
    ///
    /// WITHOUT THIS THE SOLVER CALLED ITSELF CONVERGED WHEN IT WAS STUCK. The merit test alone
    /// asks "did this step improve things much", and the answer is no both at a minimum and at a
    /// point where the damping has grown so large that every step is tiny. Those are opposite
    /// situations and only one of them is convergence. Measured on the V-coat template: outcome
    /// Converged, merit moved 453 to 446, and |J^T r| ENDED HIGHER than it started. Found by X1.6
    /// asking every template to end stationary.
    /// </summary>
    public double GradientTolerance { get; init; } = 1e-6;

    /// <summary>
    /// Per-variable bounds. A thickness below zero is not a design, it is a sign error, and an
    /// unbounded optimiser will happily produce one on the way to somewhere else. Null means the
    /// default: zero below, unbounded above. A step past a bound is reflected back inside it
    /// (Bounds.Reflect), not clamped to it.
    /// </summary>
    public double[]? Lower { get; init; }
    public double[]? Upper { get; init; }

    /// <summary>Called after every accepted step, on the thread doing the work.</summary>
    public Action<DlsProgress>? Progress { get; init; }

    /// <summary>Checked once per iteration. A stopped run ends at its best point so far with outcome
    /// Cancelled; the design is never left part way through a step.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The same options with another progress callback.</summary>
    public DlsOptions WithProgress(Action<DlsProgress>? progress) => new()
    {
        MaxIterations = MaxIterations, InitialDamping = InitialDamping, DampingUp = DampingUp,
        DampingDown = DampingDown, MeritTolerance = MeritTolerance, StepTolerance = StepTolerance,
        GradientTolerance = GradientTolerance, Lower = Lower, Upper = Upper,
        Progress = progress, Cancellation = Cancellation,
    };
}

/// <summary>One accepted step: the iteration (from 1), the merit after it, and the damping that
/// produced it.</summary>
public readonly record struct DlsProgress(int Iteration, double Merit, double Damping);

public enum DlsOutcome
{
    /// <summary>The merit stopped improving. The usual and desirable end.</summary>
    Converged,
    /// <summary>Every variable stopped moving.</summary>
    StepTooSmall,
    /// <summary>The iteration budget ran out with the merit still falling.</summary>
    IterationLimit,

    /// <summary>
    /// The merit stopped improving BEFORE the gradient reached its tolerance. Not convergence:
    /// the steps have become too small to make progress, which is what a heavily damped least
    /// squares does on a badly conditioned or strongly curved surface. Reported separately so it
    /// cannot be mistaken for the real thing — on the V-coat template the merit moved 453 to 446
    /// and the gradient ended HIGHER than it started, and the old code called that Converged. A
    /// global search is usually the answer.
    /// </summary>
    Stalled,
    /// <summary>No damping, however large, produced a step that improved the merit.</summary>
    NoDownhillStep,
    /// <summary>Stopped by the caller; the design is at the best point reached.</summary>
    Cancelled,
}

/// <param name="Evaluations">Value-only calls: tf_evaluate.</param>
/// <param name="Jacobians">
/// Jacobian calls: tf_evaluate_jacobian. Counted separately because the two cost very different
/// amounts and the ratio between them is what decides whether a faster derivative sweep is worth
/// writing — see X1.5.
/// </param>
public readonly record struct DlsResult(
    DlsOutcome Outcome, double InitialMerit, double FinalMerit, int Iterations,
    int Evaluations, int Jacobians);

public sealed class DampedLeastSquares
{
    private readonly int _n;                 // variables
    private readonly int _m;                 // residuals
    private readonly double[] _jtj;          // [n*n] the normal matrix, row-major
    private readonly double[] _chol;         // [n*n] its factor
    private readonly double[] _jtr;          // [n]
    private readonly double[] _delta;        // [n]
    private readonly double[] _diag;         // [n] diag(J^T J), kept for the Marquardt scaling
    private double _gradientNorm;            // |J^T r|_inf at the current point
    private double _diagMax;                 // the largest of _diag
    private readonly double[] _best;         // [n] the variables of the best point so far
    private readonly double[] _lower;
    private readonly double[] _upper;

    public DampedLeastSquares(int variableCount, int residualCount, DlsOptions? options = null)
    {
        options ??= new DlsOptions();
        _n = variableCount;
        _m = residualCount;
        _jtj = new double[_n * _n];
        _chol = new double[_n * _n];
        _jtr = new double[_n];
        _delta = new double[_n];
        _diag = new double[_n];
        _best = new double[_n];

        // A BOUNDS ARRAY OF THE WRONG LENGTH IS A SHAPE MISMATCH, and it used to be an index out
        // of range thrown from a constructor with nothing to say about why. It happens for a real
        // reason: a needle insertion grows the variable count, so bounds built for the stack that
        // went in no longer describe the one that came out.
        if (options.Lower is { } lower && lower.Length != _n)
            throw new ArgumentException(
                $"the lower bounds describe {lower.Length} variables and this solver has {_n}. " +
                "A needle insertion or a layer removal changes the variable count, so the bounds " +
                "have to be rebuilt with it.", nameof(options));
        if (options.Upper is { } upper && upper.Length != _n)
            throw new ArgumentException(
                $"the upper bounds describe {upper.Length} variables and this solver has {_n}.",
                nameof(options));

        _lower = new double[_n];
        _upper = new double[_n];
        for (int i = 0; i < _n; ++i)
        {
            _lower[i] = options.Lower is { } lo ? lo[i] : 0.0;
            _upper[i] = options.Upper is { } up ? up[i] : double.PositiveInfinity;
        }
    }

    public DlsResult Minimise(Design design, DlsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        options ??= new DlsOptions();

        if (design.VariableCount != _n)
            throw new ArgumentException(
                $"this solver was built for {_n} variables and the Design has {design.VariableCount}",
                nameof(design));

        // A START OUTSIDE ITS BOUNDS cannot be reflected: mirroring a small step about a bound it
        // is far from would throw it as far again the other side (59 nm under a 700 nm minimum
        // would land near 1340). It is placed on the nearest bound first - the one move that is
        // a clamp - and the merit reported as the start is the one before, so the cost is seen.
        design.Evaluate();
        double initial = design.Merit;
        for (int i = 0; i < _n; ++i)
        {
            double v = design.GetVariable(i);
            if (v < _lower[i] || v > _upper[i]) design.SetVariable(i, Math.Clamp(v, _lower[i], _upper[i]));
        }

        design.EvaluateJacobian();
        double merit = design.Merit;
        int evaluations = 0, jacobians = 1;
        double lambda = options.InitialDamping;

        for (int i = 0; i < _n; ++i) _best[i] = design.GetVariable(i);

        int iteration = 0;
        var outcome = DlsOutcome.IterationLimit;

        double initialGradient = 0.0;

        for (; iteration < options.MaxIterations; ++iteration)
        {
            if (options.Cancellation.IsCancellationRequested)
            { outcome = DlsOutcome.Cancelled; break; }

            BuildNormalEquations(design);
            if (iteration == 0) initialGradient = _gradientNorm;

            // STATIONARY IS A STATEMENT ABOUT THE GRADIENT, not about the last step's size.
            if (_gradientNorm <= options.GradientTolerance * Math.Max(initialGradient, 1e-300))
            { outcome = DlsOutcome.Converged; break; }

            bool stepped = false;
            // Raise the damping until a step both factors AND goes downhill. The inner limit is
            // generous because each attempt costs one evaluation, not one Jacobian.
            for (int attempt = 0; attempt < 32; ++attempt)
            {
                if (!TrySolve(lambda))
                {
                    lambda *= options.DampingUp;
                    continue;
                }

                // Reflected, not clamped, at a bound (Bounds.cs); a step that overshoots by more
                // than a variable's whole range is too long to reflect, and is retried shorter.
                bool tooLong = false;
                for (int i = 0; i < _n && !tooLong; ++i)
                    tooLong = Bounds.Reflect(_best[i] + _delta[i], _lower[i], _upper[i]) is null;
                if (tooLong)
                {
                    lambda *= options.DampingUp;
                    continue;
                }

                double largest = 0.0;
                for (int i = 0; i < _n; ++i)
                {
                    double v = Bounds.Reflect(_best[i] + _delta[i], _lower[i], _upper[i])!.Value;
                    design.SetVariable(i, v);
                    largest = Math.Max(largest, Math.Abs(v - _best[i]));
                }

                design.Evaluate();
                ++evaluations;

                if (design.Merit < merit)
                {
                    double improvement = (merit - design.Merit) / Math.Max(merit, double.Epsilon);
                    merit = design.Merit;
                    for (int i = 0; i < _n; ++i) _best[i] = design.GetVariable(i);
                    options.Progress?.Invoke(new DlsProgress(iteration + 1, merit, lambda));
                    lambda = Math.Max(lambda * options.DampingDown, 1e-14);
                    stepped = true;

                    // A stalled step and a converged one look identical from the merit alone, so
                    // the gradient decides which this is.
                    bool small = _gradientNorm
                                 <= options.GradientTolerance * Math.Max(initialGradient, 1e-300);
                    if (largest < options.StepTolerance)
                        outcome = small ? DlsOutcome.Converged : DlsOutcome.StepTooSmall;
                    else if (improvement < options.MeritTolerance)
                        outcome = small ? DlsOutcome.Converged : DlsOutcome.Stalled;
                    else outcome = DlsOutcome.IterationLimit;
                    break;
                }

                // Uphill: put the best point back before trying a heavier damping, so the next
                // trial step starts from where the design actually is.
                for (int i = 0; i < _n; ++i) design.SetVariable(i, _best[i]);
                lambda *= options.DampingUp;
            }

            if (!stepped)
            {
                bool small = _gradientNorm
                             <= options.GradientTolerance * Math.Max(initialGradient, 1e-300);
                outcome = small ? DlsOutcome.Converged : DlsOutcome.NoDownhillStep;
                break;
            }
            if (outcome is DlsOutcome.Converged or DlsOutcome.StepTooSmall
                        or DlsOutcome.Stalled) break;

            design.EvaluateJacobian();
            ++jacobians;
        }

        // Leave the design AT ITS BEST POINT, not wherever the last trial step landed. An
        // optimiser that returns a worse design than it found is a bug people discover slowly.
        for (int i = 0; i < _n; ++i) design.SetVariable(i, _best[i]);
        design.Evaluate();

        return new DlsResult(outcome, initial, design.Merit, iteration + 1,
                             evaluations + 1, jacobians);
    }

    /// <summary>
    /// J^T J and J^T r, from the Jacobian the engine just produced. COLUMN-MAJOR: the ABI stores
    /// J[v * n_residuals + r], so a variable's column is contiguous — which is what makes these
    /// two products a walk down memory rather than a stride across it.
    /// </summary>
    private void BuildNormalEquations(Design design)
    {
        var j = design.Jacobian;
        var r = design.Residual;

        System.Array.Clear(_jtj);
        System.Array.Clear(_jtr);
        _gradientNorm = 0.0;

        for (int a = 0; a < _n; ++a)
        {
            int ca = a * _m;
            double dot = 0.0;
            for (int k = 0; k < _m; ++k) dot += j[ca + k] * r[k];
            _jtr[a] = -dot;                        // the right-hand side is -J^T r
            if (Math.Abs(dot) > _gradientNorm) _gradientNorm = Math.Abs(dot);

            for (int b = a; b < _n; ++b)
            {
                int cb = b * _m;
                double s = 0.0;
                for (int k = 0; k < _m; ++k) s += j[ca + k] * j[cb + k];
                _jtj[a * _n + b] = s;
                _jtj[b * _n + a] = s;
            }
            _diag[a] = _jtj[a * _n + a];
        }
        _diagMax = _diag.Length > 0 ? _diag.Max() : 0.0;
    }

    /// <summary>
    /// Cholesky on (J^T J + lambda * diag(J^T J)), then two triangular solves. Returns false when
    /// the matrix is not positive definite at this damping, which is the signal to raise it.
    /// </summary>
    private bool TrySolve(double lambda)
    {
        for (int i = 0; i < _n * _n; ++i) _chol[i] = _jtj[i];
        for (int i = 0; i < _n; ++i)
        {
            // A variable with an identically zero column — an inert one, which the plan reports —
            // has a zero diagonal and would make the matrix singular at any damping. Giving it a
            // floor leaves it fixed rather than making the whole step impossible.
            //
            // AND A COLUMN THAT IS ZERO TO ROUNDING IS ZERO. The deepest layer of a high reflector
            // moves nothing light reaches, and its column comes back as 1e-16 noise rather than an
            // exact 0 (the chirped mirror's last layer under a GDD row, found 2026-10-23). Damped
            // by its own 1e-31 diagonal, that variable took steps of hundreds of nm on noise and
            // every trial went uphill until the damping reached 1e13. Below 1e-12 of the stiffest
            // column a variable is treated as inert, and damped as hard as the stiffest.
            double d = _diag[i] > 1e-12 * _diagMax ? _diag[i] : _diagMax > 0.0 ? _diagMax : 1.0;
            _chol[i * _n + i] += lambda * d;
        }

        for (int i = 0; i < _n; ++i)
        {
            for (int k = 0; k < i; ++k) _chol[i * _n + i] -= _chol[i * _n + k] * _chol[i * _n + k];
            if (_chol[i * _n + i] <= 0.0) return false;
            _chol[i * _n + i] = Math.Sqrt(_chol[i * _n + i]);

            for (int jx = i + 1; jx < _n; ++jx)
            {
                double s = _chol[jx * _n + i];
                for (int k = 0; k < i; ++k) s -= _chol[jx * _n + k] * _chol[i * _n + k];
                _chol[jx * _n + i] = s / _chol[i * _n + i];
            }
        }

        for (int i = 0; i < _n; ++i)
        {
            double s = _jtr[i];
            for (int k = 0; k < i; ++k) s -= _chol[i * _n + k] * _delta[k];
            _delta[i] = s / _chol[i * _n + i];
        }
        for (int i = _n - 1; i >= 0; --i)
        {
            double s = _delta[i];
            for (int k = i + 1; k < _n; ++k) s -= _chol[k * _n + i] * _delta[k];
            _delta[i] = s / _chol[i * _n + i];
        }

        for (int i = 0; i < _n; ++i)
            if (double.IsNaN(_delta[i]) || double.IsInfinity(_delta[i])) return false;

        return true;
    }
}
