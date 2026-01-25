using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;



public static class StringBuilderExtensions
{
	public static void Append(this StringBuilder stringBuilder, ReadOnlySpan<char> part)
	{
		foreach (var c in part) { stringBuilder.Append(c); }
	}
}

public static class SpanExtensions
{
	public static SpanSplitEnumerator<T> SplitAfter<T>(this ReadOnlySpan<T> source, T separator)
		 where T : IEquatable<T>
		 => new SpanSplitEnumerator<T>(source, separator);
}

public ref struct SpanSplitEnumerator<T>
	 where T : IEquatable<T>
{
	private ReadOnlySpan<T> span;
	private readonly T sep;
	private int pos;

	internal SpanSplitEnumerator(ReadOnlySpan<T> span, T separator)
	{
		this.span = span;
		this.sep = separator;
		this.pos = 0;
		this.Current = default;
	}

	// Allow `foreach`
	public SpanSplitEnumerator<T> GetEnumerator() => this;


	public bool HasNext()
	{
		if (this.pos > this.span.Length)
			return false;
		else return true;
	}

	// Advance to next slice
	public bool MoveNext()
	{
		if (this.pos > this.span.Length)
			return false;

		// Find separator in the remaining slice
		var remaining = this.span.Slice(this.pos);
		var idx = remaining.IndexOf(this.sep);


		if (idx < 0)
		{
			// last segment
			this.Current = remaining;
			this.pos = this.span.Length + 1;
			return true;
		}

		if (idx + 1 > remaining.Length)
		{
			idx = remaining.Length;

		}
		else
		{
			idx++;
		}

		// segment up to separator
		this.Current = remaining.Slice(0, idx);
		this.pos += idx ;
		return true;
	}

	public ReadOnlySpan<T> Current { get; private set; }
}
