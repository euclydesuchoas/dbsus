using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client;

/// <summary>
/// A Brazilian state or the Federal District (<i>unidade da federação</i>, UF), as DATASUS uses
/// them to split its files. Each member is named by the state's two-letter abbreviation, which is
/// what file names carry, and its value is the state's IBGE code.
/// </summary>
/// <remarks>
/// <para>
/// <c>default(DatasusUf)</c> (zero) is not a state: no member has that value, and the methods that
/// take a <see cref="DatasusUf"/> reject it with an <see cref="ArgumentOutOfRangeException"/>.
/// </para>
/// <para>
/// There is no member for the whole country: files named with <c>BR</c> are outside the catalog of
/// this version.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "The values are the IBGE codes of the states; there is no state zero, and an unset value must be rejected rather than named.")]
public enum DatasusUf
{
    /// <summary>Rondônia.</summary>
    RO = 11,

    /// <summary>Acre.</summary>
    AC = 12,

    /// <summary>Amazonas.</summary>
    AM = 13,

    /// <summary>Roraima.</summary>
    RR = 14,

    /// <summary>Pará.</summary>
    PA = 15,

    /// <summary>Amapá.</summary>
    AP = 16,

    /// <summary>Tocantins.</summary>
    TO = 17,

    /// <summary>Maranhão.</summary>
    MA = 21,

    /// <summary>Piauí.</summary>
    PI = 22,

    /// <summary>Ceará.</summary>
    CE = 23,

    /// <summary>Rio Grande do Norte.</summary>
    RN = 24,

    /// <summary>Paraíba.</summary>
    PB = 25,

    /// <summary>Pernambuco.</summary>
    PE = 26,

    /// <summary>Alagoas.</summary>
    AL = 27,

    /// <summary>Sergipe.</summary>
    SE = 28,

    /// <summary>Bahia.</summary>
    BA = 29,

    /// <summary>Minas Gerais.</summary>
    MG = 31,

    /// <summary>Espírito Santo.</summary>
    ES = 32,

    /// <summary>Rio de Janeiro.</summary>
    RJ = 33,

    /// <summary>São Paulo.</summary>
    SP = 35,

    /// <summary>Paraná.</summary>
    PR = 41,

    /// <summary>Santa Catarina.</summary>
    SC = 42,

    /// <summary>Rio Grande do Sul.</summary>
    RS = 43,

    /// <summary>Mato Grosso do Sul.</summary>
    MS = 50,

    /// <summary>Mato Grosso.</summary>
    MT = 51,

    /// <summary>Goiás.</summary>
    GO = 52,

    /// <summary>Distrito Federal.</summary>
    DF = 53,
}

/// <summary>The two-letter abbreviations of the states, as file names carry them.</summary>
internal static class DatasusUfNames
{
    /// <summary>Every state, in the order of the IBGE codes.</summary>
    public static readonly DatasusUf[] All =
    [
        DatasusUf.RO, DatasusUf.AC, DatasusUf.AM, DatasusUf.RR, DatasusUf.PA, DatasusUf.AP, DatasusUf.TO,
        DatasusUf.MA, DatasusUf.PI, DatasusUf.CE, DatasusUf.RN, DatasusUf.PB, DatasusUf.PE, DatasusUf.AL, DatasusUf.SE, DatasusUf.BA,
        DatasusUf.MG, DatasusUf.ES, DatasusUf.RJ, DatasusUf.SP,
        DatasusUf.PR, DatasusUf.SC, DatasusUf.RS,
        DatasusUf.MS, DatasusUf.MT, DatasusUf.GO, DatasusUf.DF,
    ];

    private static readonly Dictionary<string, DatasusUf> ByName = BuildByName();

    /// <summary>Gets the abbreviation of a state, or <see langword="null"/> for a value that is not a state.</summary>
    public static string? NameOf(DatasusUf uf) => uf switch
    {
        DatasusUf.RO => "RO",
        DatasusUf.AC => "AC",
        DatasusUf.AM => "AM",
        DatasusUf.RR => "RR",
        DatasusUf.PA => "PA",
        DatasusUf.AP => "AP",
        DatasusUf.TO => "TO",
        DatasusUf.MA => "MA",
        DatasusUf.PI => "PI",
        DatasusUf.CE => "CE",
        DatasusUf.RN => "RN",
        DatasusUf.PB => "PB",
        DatasusUf.PE => "PE",
        DatasusUf.AL => "AL",
        DatasusUf.SE => "SE",
        DatasusUf.BA => "BA",
        DatasusUf.MG => "MG",
        DatasusUf.ES => "ES",
        DatasusUf.RJ => "RJ",
        DatasusUf.SP => "SP",
        DatasusUf.PR => "PR",
        DatasusUf.SC => "SC",
        DatasusUf.RS => "RS",
        DatasusUf.MS => "MS",
        DatasusUf.MT => "MT",
        DatasusUf.GO => "GO",
        DatasusUf.DF => "DF",
        _ => null,
    };

    /// <summary>Finds the state with the given abbreviation, ignoring case.</summary>
    public static bool TryParse(string name, out DatasusUf uf) => ByName.TryGetValue(name, out uf);

    /// <summary>Finds the state whose abbreviation is the two letters at <paramref name="index"/>, ignoring case, without allocating.</summary>
    public static bool TryParse(string text, int index, out DatasusUf uf)
    {
        var key = (Upper(text[index]) << 8) | Upper(text[index + 1]);
        foreach (var candidate in All)
        {
            var name = NameOf(candidate)!;
            if (((name[0] << 8) | name[1]) == key)
            {
                uf = candidate;
                return true;
            }
        }

        uf = default;
        return false;

        static int Upper(char c) => c is >= 'a' and <= 'z' ? c - 32 : c;
    }

    /// <summary>Rejects a value that is not a state.</summary>
    public static string Require(DatasusUf uf, string paramName) =>
        NameOf(uf) ?? throw new ArgumentOutOfRangeException(
            paramName,
            uf,
            "The value is not a Brazilian state; default(DatasusUf) is not one either.");

    private static Dictionary<string, DatasusUf> BuildByName()
    {
        var names = new Dictionary<string, DatasusUf>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in All)
        {
            names[NameOf(value)!] = value;
        }

        return names;
    }
}
