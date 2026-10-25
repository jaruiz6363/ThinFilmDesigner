// PinnedBuffer.cs — the allocation primitive everything above this line is built from.
//
// C0.1 SAYS EVERY BUFFER IS DIMENSIONED AND ALLOCATED IN C# AND PASSED TO C++. That makes the
// managed side the owner of memory the native side will hold pointers into across a call, and it
// makes the lifetime question real: a garbage collector that compacts the heap while a pointer is
// in a struct on the stack produces a wild pointer with no symptom until it has one.
//
// WHY THE PINNED OBJECT HEAP AND NOT GCHandle OR fixed.
//
//   fixed      scopes the pin to a block. A plan buffer has to stay put from tf_plan until the
//              last evaluation that reads it, which can be an entire optimisation, so every call
//              would have to nest inside one fixed block per buffer — a dozen of them, by hand,
//              in the right order, at every call site. That is a correctness property enforced by
//              indentation, which is not enforcement.
//
//   GCHandle   pins an object that lives on the ordinary heap, so the collector has to work
//              around it forever. A few dozen long-lived pinned arrays in the middle of gen 0 is
//              exactly the fragmentation pattern pinning is warned about.
//
//   POH        GC.AllocateArray(pinned: true) allocates on the pinned object heap: never moved,
//              never compacted, no handle to leak, and no hole left in the normal heap because it
//              was never in it. The address is stable for the life of the array, which is the
//              property the native side needs, and it is a property of the ALLOCATION rather than
//              of a scope somebody has to remember to open.
//
// So a buffer is pinned by virtue of existing, and the failure mode where someone forgets to pin
// cannot be expressed: there is no unpinned way to make one of these.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThinFilm.Native;

/// <summary>
/// A managed array that never moves, plus the pointer the native side is given. Owns nothing
/// unmanaged, so there is nothing to free and nothing to leak — the array is collected when the
/// last reference goes, exactly like any other.
/// </summary>
public sealed unsafe class PinnedBuffer<T> where T : unmanaged
{
    private readonly T[] _array;

    /// <param name="count">
    /// Element count, straight from tf_plan_sizes or tf_plan_report. Zero is legal and common —
    /// a problem with no variables needs no variable_inert — and yields a null pointer, which is
    /// what the ABI expects for an absent buffer rather than a pointer to nothing.
    /// </param>
    public PinnedBuffer(long count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "a buffer count cannot be negative");
        if (count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "a single buffer above int.MaxValue elements is beyond what one array can hold");

        Count = count;
        _array = count == 0 ? System.Array.Empty<T>() : GC.AllocateArray<T>((int)count, pinned: true);
    }

    public long Count { get; }

    /// <summary>The managed view, for reading and writing the contents normally.</summary>
    public Span<T> Span => _array;

    /// <summary>
    /// The address the native side is given. Null when the buffer is empty, which is the ABI's
    /// own convention for "this one is not supplied" and keeps the two meanings from diverging.
    /// </summary>
    public T* Pointer
    {
        get
        {
            if (Count == 0) return null;
            return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_array));
        }
    }

    public ref T this[long i] => ref _array[i];

    /// <summary>Copies a managed array into a fresh pinned one. The usual way inputs are built.</summary>
    public static PinnedBuffer<T> From(ReadOnlySpan<T> source)
    {
        var b = new PinnedBuffer<T>(source.Length);
        source.CopyTo(b.Span);
        return b;
    }

    public static PinnedBuffer<T> From(params T[] source) => From((ReadOnlySpan<T>)source);
}
