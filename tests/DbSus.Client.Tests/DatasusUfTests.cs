using System.Reflection;
using Xunit;

namespace DbSus.Client.Tests;

public class DatasusUfTests
{
    // The IBGE codes of the 26 states and the Federal District.
    [Theory]
    [InlineData(DatasusUf.RO, 11, "RO")]
    [InlineData(DatasusUf.AC, 12, "AC")]
    [InlineData(DatasusUf.AM, 13, "AM")]
    [InlineData(DatasusUf.RR, 14, "RR")]
    [InlineData(DatasusUf.PA, 15, "PA")]
    [InlineData(DatasusUf.AP, 16, "AP")]
    [InlineData(DatasusUf.TO, 17, "TO")]
    [InlineData(DatasusUf.MA, 21, "MA")]
    [InlineData(DatasusUf.PI, 22, "PI")]
    [InlineData(DatasusUf.CE, 23, "CE")]
    [InlineData(DatasusUf.RN, 24, "RN")]
    [InlineData(DatasusUf.PB, 25, "PB")]
    [InlineData(DatasusUf.PE, 26, "PE")]
    [InlineData(DatasusUf.AL, 27, "AL")]
    [InlineData(DatasusUf.SE, 28, "SE")]
    [InlineData(DatasusUf.BA, 29, "BA")]
    [InlineData(DatasusUf.MG, 31, "MG")]
    [InlineData(DatasusUf.ES, 32, "ES")]
    [InlineData(DatasusUf.RJ, 33, "RJ")]
    [InlineData(DatasusUf.SP, 35, "SP")]
    [InlineData(DatasusUf.PR, 41, "PR")]
    [InlineData(DatasusUf.SC, 42, "SC")]
    [InlineData(DatasusUf.RS, 43, "RS")]
    [InlineData(DatasusUf.MS, 50, "MS")]
    [InlineData(DatasusUf.MT, 51, "MT")]
    [InlineData(DatasusUf.GO, 52, "GO")]
    [InlineData(DatasusUf.DF, 53, "DF")]
    public void Member_HasTheIbgeCode_AndTheAbbreviationAsItsName(DatasusUf uf, int ibgeCode, string abbreviation)
    {
        Assert.Equal(ibgeCode, (int)uf);
        Assert.Equal(abbreviation, uf.ToString());
        Assert.Equal(abbreviation, DatasusUfNames.NameOf(uf));
        Assert.True(DatasusUfNames.TryParse(abbreviation.ToLowerInvariant(), out var parsed));
        Assert.Equal(uf, parsed);
    }

    [Fact]
    public void Enum_HasExactly27Members_AndNoneIsZero()
    {
        var declared = typeof(DatasusUf).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (DatasusUf)field.GetValue(null)!).ToArray();

        Assert.Equal(27, declared.Length);
        Assert.Equal(27, declared.Distinct().Count());
        Assert.DoesNotContain(default, declared);
        Assert.Equal(declared.OrderBy(uf => (int)uf), DatasusUfNames.All);
    }

    [Theory]
    [InlineData((DatasusUf)0)]
    [InlineData((DatasusUf)34)]
    [InlineData((DatasusUf)99)]
    public void Require_ValueThatIsNotAState_Throws(DatasusUf uf)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => DatasusUfNames.Require(uf, nameof(uf)));

        Assert.Equal(nameof(uf), exception.ParamName);
        Assert.Null(DatasusUfNames.NameOf(uf));
    }

    [Theory]
    [InlineData("BR")]
    [InlineData("EX")]
    [InlineData("UF")]
    [InlineData("PM")]
    [InlineData("")]
    [InlineData("SPX")]
    public void TryParse_NameThatIsNotAState_ReturnsFalse(string name)
    {
        Assert.False(DatasusUfNames.TryParse(name, out _));
    }

    [Fact]
    public void TryParseAt_EveryState_InAnyLetterCase_AtAnyIndex()
    {
        foreach (var uf in DatasusUfNames.All)
        {
            var name = DatasusUfNames.NameOf(uf)!;
            var lower = name.ToLowerInvariant();
            foreach (var text in new[] { name, lower, $"{name[0]}{lower[1]}", $"{lower[0]}{name[1]}" })
            {
                Assert.True(DatasusUfNames.TryParse(text, 0, out var atStart), text);
                Assert.Equal(uf, atStart);
                Assert.True(DatasusUfNames.TryParse("PA" + text + "2407.dbc", 2, out var inside), text);
                Assert.Equal(uf, inside);
            }
        }
    }

    [Theory]
    [InlineData("R\u0150")]    // O with double acute; read as SP while the two characters were packed into one number
    [InlineData("S\u0150")]
    [InlineData("\u0153P")]    // the ligature oe
    [InlineData("\u0100C")]    // A with macron
    [InlineData("M\u0147")]    // N with caron; read as MG
    [InlineData("BR")]
    [InlineData("XX")]
    [InlineData("S ")]
    [InlineData("24")]
    public void TryParseAt_TwoCharactersThatAreNotAState_ReturnsFalse(string text)
    {
        Assert.False(DatasusUfNames.TryParse(text, 0, out var uf), $"U+{(int)text[0]:X4} U+{(int)text[1]:X4} read as {uf}");
        Assert.Equal(default, uf);
    }

    // A character above U+00FF must never be taken for a letter of a state: for every state, each
    // letter is replaced by the characters that share its low byte, and the first letter also by
    // the ones that would complete it if the two were packed into one number.
    [Fact]
    public void TryParseAt_CharactersThatShareTheLowByteOfALetter_AreNotAState()
    {
        foreach (var uf in DatasusUfNames.All)
        {
            var name = DatasusUfNames.NameOf(uf)!;
            for (var high = 1; high <= 0xFF; high++)
            {
                var second = (char)(name[1] | (high << 8));
                var first = (char)(name[0] | (high << 8));
                var firstWithout = (char)(name[0] & ~high);
                foreach (var text in new[] { $"{name[0]}{second}", $"{firstWithout}{second}", $"{first}{name[1]}", $"{first}{second}" })
                {
                    Assert.False(DatasusUfNames.TryParse(text, 0, out var parsed), $"U+{(int)text[0]:X4} U+{(int)text[1]:X4} read as {parsed}");
                }
            }
        }
    }
}
