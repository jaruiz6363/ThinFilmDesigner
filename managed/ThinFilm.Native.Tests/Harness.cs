// Harness.cs — the same shape of test harness the native suite uses, on purpose.
//
// No xunit, no NUnit, no package reference. Partly because a test project that restores nothing
// builds anywhere, and mostly because the native suite prints "1744 checks, 0 failures" and it
// should be possible to read the managed total in the same sentence without translating between
// two vocabularies of what a check is.

namespace ThinFilm.Native.Tests;

public sealed class Harness
{
    public int Checks { get; private set; }
    public int Failures { get; private set; }

    public void Section(string title) => Console.WriteLine(title);

    public void Note(string text) => Console.WriteLine($"        ({text})");

    public void Fail(string what)
    {
        ++Checks; ++Failures;
        Console.WriteLine($"  FAIL  {what}");
    }

    public void Expect(bool condition, string what)
    {
        ++Checks;
        if (!condition) { ++Failures; Console.WriteLine($"  FAIL  {what}"); }
    }

    /// <summary>An integer that must be the pinned one. Prints both, so a deliberate ABI bump can
    /// read its new pins straight off the failure.</summary>
    public void Pin(long got, long want, string what)
    {
        ++Checks;
        if (got != want)
        {
            ++Failures;
            Console.WriteLine($"  FAIL  {what,-52} is {got}, pinned at {want}");
        }
    }

    /// <summary>A double that must match to the last bit. No tolerance: see CallTests.</summary>
    public void PinExact(double got, double want, string what)
    {
        ++Checks;
        if (got != want)
        {
            ++Failures;
            Console.WriteLine($"  FAIL  {what,-52} is {got:R}, expected {want:R}");
        }
    }

    public void ExpectStatus(TfStatus got, TfStatus want, string what)
    {
        ++Checks;
        if (got != want)
        {
            ++Failures;
            Console.WriteLine($"  FAIL  {what,-52} returned {got} ({(int)got}), expected {want} ({(int)want})");
        }
    }
}
