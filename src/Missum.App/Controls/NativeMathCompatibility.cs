using CSharpMath;
using CSharpMath.Atom;
using CSharpMath.Atom.Atoms;

namespace Missum.App.Controls;

internal static class NativeMathCompatibility
{
    internal const string AlignedCommand = @"\missumaligned";
    private const string AlignedEnvironment = "aligned";

    internal static bool IsAlignedEnvironment(ReadOnlySpan<char> argument)
    {
        argument = argument.TrimStart();
        if (argument.IsEmpty || argument[0] != '{') return false;
        argument = argument[1..].TrimStart();
        return argument.StartsWith(AlignedEnvironment, StringComparison.Ordinal)
            && argument[AlignedEnvironment.Length..].TrimStart().StartsWith("}", StringComparison.Ordinal);
    }

    internal static void RegisterCommands()
    {
        lock (LaTeXSettings.Commands)
        {
            LaTeXSettings.Commands.Add(@"\tfrac", (parser, _, _) =>
                parser.ReadArgument().Bind(numerator => parser.ReadArgument().Bind(denominator =>
                    LaTeXSettings.Ok(new Inner(Boundary.Empty,
                        new MathList(new Style(LineStyle.Text), new Fraction(numerator, denominator)),
                        Boundary.Empty)))));
            LaTeXSettings.Commands.Add(AlignedCommand, (parser, _, stopChar) =>
                parser.ReadEnvironment().Bind(name => name == AlignedEnvironment
                    ? ReadAligned(parser, stopChar)
                    : LaTeXSettings.Err(@"Expected \begin{aligned}")));
        }
    }

    private static Result<(MathAtom? Atom, MathList? Return)> ReadAligned(LaTeXParser parser, char stopChar)
    {
        // Let CSharpMath parse the cells and recognize their delimiters, including matching \end.
        // Its built-in aligned table only configures a single equation pair.
        var environment = new LaTeXParser.TableEnvironment(AlignedEnvironment);
        var table = new Table(AlignedEnvironment) { InterRowAdditionalSpacing = 1 };
        parser.Environments.Push(environment);
        try
        {
            var column = 0;
            while (parser.HasCharacters)
            {
                var row = environment.NRows;
                var (cell, error) = parser.ReadUntil(stopChar);
                if (error is not null) return LaTeXSettings.Err(error);

                if (column % 2 == 1)
                    cell.Insert(0, new Ordinary(string.Empty)); // Preserve spacing before a relation.
                else if (column > 0)
                {
                    // The zero-size display keeps the leading space in the measured cell.
                    cell.Insert(0, new Space(Length.EmWidth * 2));
                    cell.Insert(0, new RaiseBox(Length.Point * 0, new MathList()));
                }
                table.SetCell(cell, row, column);
                table.SetAlignment(column % 2 == 0 ? ColumnAlignment.Right : ColumnAlignment.Left, column);

                if (environment.Ended) return LaTeXSettings.Ok(table);
                if (stopChar != '\0' && parser.Chars[parser.NextChar - 1] == stopChar) break;
                column = environment.NRows == row ? column + 1 : 0;
            }
            return LaTeXSettings.Err(@"Missing \end for \begin{aligned}");
        }
        finally
        {
            parser.Environments.Pop();
        }
    }
}
