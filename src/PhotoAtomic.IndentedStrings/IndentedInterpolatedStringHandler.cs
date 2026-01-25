using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoAtomic.IndentedStrings;

[InterpolatedStringHandler]
internal class IndentedInterpolatedStringHandler
{
    public static IndentedInterpolatedStringHandler Indent(IndentedInterpolatedStringHandler handler) => handler;
    public static IndentedInterpolatedStringHandler Indent(IEnumerable<string?> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts.Where(x => x is not null))
        {
            builder.Append(part);
        }
        var handler = new IndentedInterpolatedStringHandler(builder);
        return handler;

    }
    public static implicit operator string(IndentedInterpolatedStringHandler v) => v.ToString();

    protected internal StringBuilder builder;
    protected internal int spaceIndent = 0;
    protected internal int hTabIndent = 0;
    protected internal int vTabIndent = 0;
    protected internal int formFeedIndent = 0;

    public IndentedInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        this.builder = new StringBuilder(literalLength);
    }

    public IndentedInterpolatedStringHandler(StringBuilder builder)
    {
        this.builder = builder;
        (this.spaceIndent, this.hTabIndent, this.vTabIndent, this.formFeedIndent) = this.CountIndent(builder.ToString());
    }

    public void AppendLiteral(string s)
    {


        (this.spaceIndent, this.hTabIndent, this.vTabIndent, this.formFeedIndent) = this.CountIndent(s);
        //this.builder.Append(s.AsSpan(0, s.Length - (this.spaceIndent + this.hTabIndent + this.vTabIndent + this.formFeedIndent)));
        this.builder.Append(s);
    }



    public void AppendFormatted<T>(T t)
    {
        if (t is null)
        {
            if (!this.CleanTrailingWhitespace()) return;
            return;
        }
        var s = t.ToString();
        var isFirstLine = true;
        var linesEnumerator = s.AsSpan().SplitAfter('\n');
        foreach (var line in linesEnumerator)
        {
            if (!isFirstLine) this.AppendIndentation();
            isFirstLine = false;
            this.builder.Append(line);
        }
        if (!string.IsNullOrEmpty(s) && this.IsMultiLine(s)) this.CleanTrailingWhitespace();
    }

    private bool IsMultiLine(string s)
    {
        foreach (var c in s)
        {
            if (c == '\n' || c == '\r') return true;
        }
        return false;
    }

    private bool CleanTrailingWhitespace()
    {
        var i = this.builder.Length - 1;
        for (; i >= 0; i--)
        {
            var c = this.builder[i];
            if (c == ' ' ||
                c == '\t' ||
                c == '\v' ||
                c == '\f' ||
                c == '\n' ||
                c == '\r') continue;
            else
            {
                break;
            }
        }
        if (i == this.builder.Length - 1) return false;
        this.builder.Remove(i + 1, this.builder.Length - 1 - i);
        return true;
    }

    private void AppendIndentation()
    {
        for (var hTab = 0; hTab < this.hTabIndent; hTab++)
        {
            this.builder.Append('\t');
        }
        for (var vTab = 0; vTab < this.vTabIndent; vTab++)
        {
            this.builder.Append('\v');
        }
        for (var form = 0; form < this.formFeedIndent; form++)
        {
            this.builder.Append('\f');
        }
        for (var space = 0; space < this.spaceIndent; space++)
        {
            this.builder.Append(' ');
        }
    }

    internal string GetFormattedText() => this.builder.ToString();


    public override string ToString() => this.GetFormattedText();

    private (int spaceIndent, int hTabIndent, int vTabIndent, int formFeedIndent) CountIndent(string s)
    {
        var spaceIndent = 0;
        var hTabIndent = 0;
        var vTabIndent = 0;
        var formFeedIndent = 0;

        if (s.Length == 0) return (0, 0, 0, 0);

        var pos = s.Length - 1;

        while ((s[pos] == '\r' || s[pos] == '\n') && pos > 0)
        {
            pos--;
        }

        for (var i = pos; i >= 0; i--)
        {
            if (s[i] == '\n' || s[i] == '\r') break;
            if (s[i] == ' ')
            {
                spaceIndent++;
            }
            else if (s[i] == '\t')
            {
                hTabIndent++;
            }
            else if (s[i] == '\v')
            {
                vTabIndent++;
            }
            else if (s[i] == '\f')
            {
                formFeedIndent++;
            }
            else
            {
                spaceIndent = 0;
                hTabIndent = 0;
                vTabIndent = 0;
                formFeedIndent = 0;
            }
        }


        return (spaceIndent, hTabIndent, vTabIndent, formFeedIndent);
    }
}

