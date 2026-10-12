using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client;

/// <summary>
/// A partial catalog of the file types DATASUS lists: 42 of SIASUS, SIHSUS, CNES, SIM, SINASC,
/// CIHA, CIH, SISPRENATAL, SISCOLO, SISMAMA, PCE and RESP, by state, each as a <see cref="DatasusDataset"/>.
/// Use a property, e.g. <c>DatasusCatalog.SihSus.RD</c>, or look one up by text with <see cref="Get"/>.
/// </summary>
/// <remarks>
/// <para>
/// Most of the file types DATASUS lists are not here: those of other sources (SINAN, for one) and
/// other file types of these sources, the ones that only have a file for the whole country
/// included. A file type that is missing here may still be on the server.
/// </para>
/// <para>
/// Four of the file types also have a file for the whole country: SIM-DO, SINASC-DN, PCE and
/// RESP (see <see cref="DatasusDataset.GetNationalPath"/>).
/// </para>
/// <para>
/// Six of the file types also have the folder layout that came before the one in use now:
/// SIASUS-PA from 1994-07, SIHSUS RD, SP and RJ from 1992-01, SIM-DO from 1979 and SINASC-DN from
/// 1994.
/// </para>
/// <para>
/// What is outside the catalog can be reached with the path as a string, when you know it. The
/// <see href="https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md">paths guide</see>
/// says what is left out and, for what was seen on the server, where it is.
/// </para>
/// </remarks>
public static class DatasusCatalog
{
    /// <summary>Gets every dataset of the catalog, grouped by source.</summary>
    public static IReadOnlyList<DatasusDataset> Datasets { get; } =
    [
        SiaSus.PA, SiaSus.AD, SiaSus.AM, SiaSus.AQ, SiaSus.AR, SiaSus.PS, SiaSus.ATD, SiaSus.ACF,
        SiaSus.AB, SiaSus.ABO, SiaSus.AN, SiaSus.SAD, SiaSus.AMP,
        SihSus.RD, SihSus.SP, SihSus.RJ, SihSus.ER,
        Cnes.LT, Cnes.EQ, Cnes.ST, Cnes.PF, Cnes.SR, Cnes.HB, Cnes.DC, Cnes.EP, Cnes.EF, Cnes.GM, Cnes.IN, Cnes.RC, Cnes.EE,
        Sim.DO,
        Sinasc.DN,
        Ciha.CIHA,
        Cih.CR,
        SisPrenatal.PN,
        SisColo.CC, SisColo.HC,
        SisMama.CM, SisMama.HM, SisMama.MM,
        Pce.PCE,
        Resp.RESP,
    ];

    // Declared after Datasets: static fields are initialized in the order they are written.
    private static readonly Dictionary<string, DatasusDataset> ByKey = BuildIndex();

    /// <summary>Gets a dataset by its source and code, ignoring case, e.g. <c>Get("SIHSUS", "RD")</c>.</summary>
    /// <param name="source">The source, e.g. <c>SIHSUS</c>.</param>
    /// <param name="code">The code of the file type, e.g. <c>RD</c>.</param>
    /// <returns>The dataset.</returns>
    /// <exception cref="ArgumentException">
    /// The catalog has no such dataset. The message lists the ones it has and says how other files
    /// are reached.
    /// </exception>
    public static DatasusDataset Get(string source, string code)
    {
        if (TryGet(source, code, out var dataset))
        {
            return dataset;
        }

        var known = string.Join("; ", Datasets.GroupBy(item => item.Source).Select(group => group.Key + ": " + string.Join(", ", group.Select(item => item.Code))));
        throw new ArgumentException(
            $"The catalog has no dataset '{source}-{code}'. The catalog is partial; it has {known}. "
            + "A file type that is not in it may still be on the server: use its path as a string. "
            + $"What the catalog leaves out is described in {DatasusDataset.PathsGuide}",
            nameof(code));
    }

    /// <summary>Looks a dataset up by its source and code, ignoring case.</summary>
    /// <param name="source">The source, e.g. <c>SIHSUS</c>.</param>
    /// <param name="code">The code of the file type, e.g. <c>RD</c>.</param>
    /// <param name="dataset">The dataset, or <see langword="null"/> when the catalog has none.</param>
    /// <returns><see langword="true"/> when the catalog has the dataset.</returns>
    public static bool TryGet(string source, string code, [NotNullWhen(true)] out DatasusDataset? dataset)
    {
        Guard.NotNull(source, nameof(source));
        Guard.NotNull(code, nameof(code));
        return ByKey.TryGetValue(source + "-" + code, out dataset);
    }

    private static Dictionary<string, DatasusDataset> BuildIndex()
    {
        var index = new Dictionary<string, DatasusDataset>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataset in Datasets)
        {
            index.Add(dataset.ToString(), dataset);
        }

        return index;
    }

    /// <summary>SIASUS, the outpatient information system (<i>Sistema de Informações Ambulatoriais do SUS</i>). Monthly files, one per state.</summary>
    public static class SiaSus
    {
        private const string Directory = "/SIASUS/200801_/Dados";
        private const string OlderDirectory = "/SIASUS/199407_200712/Dados";

        /// <summary>
        /// Gets SIASUS-PA, <i>Produção Ambulatorial</i>, from 1994-07 on; up to 2007-12 its files are in
        /// an older folder. Split into lettered parts in the larger states.
        /// </summary>
        public static DatasusDataset PA { get; } = DatasusDataset.Monthly("SIASUS", "PA", "Produção Ambulatorial", Directory, 2008, 1, OlderDirectory, 1994, 7);

        /// <summary>Gets SIASUS-AD, <i>APAC de Laudos Diversos</i>.</summary>
        public static DatasusDataset AD { get; } = DatasusDataset.Monthly("SIASUS", "AD", "APAC de Laudos Diversos", Directory, 2008, 1);

        /// <summary>Gets SIASUS-AM, <i>APAC de Medicamentos</i>.</summary>
        public static DatasusDataset AM { get; } = DatasusDataset.Monthly("SIASUS", "AM", "APAC de Medicamentos", Directory, 2008, 1);

        /// <summary>Gets SIASUS-AQ, <i>APAC de Quimioterapia</i>.</summary>
        public static DatasusDataset AQ { get; } = DatasusDataset.Monthly("SIASUS", "AQ", "APAC de Quimioterapia", Directory, 2008, 1);

        /// <summary>Gets SIASUS-AR, <i>APAC de Radioterapia</i>.</summary>
        public static DatasusDataset AR { get; } = DatasusDataset.Monthly("SIASUS", "AR", "APAC de Radioterapia", Directory, 2008, 1);

        /// <summary>Gets SIASUS-PS, <i>Psicossocial</i>.</summary>
        public static DatasusDataset PS { get; } = DatasusDataset.Monthly("SIASUS", "PS", "Psicossocial", Directory, 2008, 1);

        /// <summary>Gets SIASUS-ATD, <i>APAC Tratamento Dialítico</i>.</summary>
        public static DatasusDataset ATD { get; } = DatasusDataset.Monthly("SIASUS", "ATD", "APAC Tratamento Dialítico", Directory, 2008, 1);

        /// <summary>Gets SIASUS-ACF, <i>APAC Confeção de Fístula Arteriovenosa</i> (spelled as DATASUS spells it).</summary>
        public static DatasusDataset ACF { get; } = DatasusDataset.Monthly("SIASUS", "ACF", "APAC Confeção de Fístula Arteriovenosa", Directory, 2008, 1);

        /// <summary>Gets SIASUS-AB, <i>APAC de Acompanhamento a Cirurgia Bariátrica</i>.</summary>
        public static DatasusDataset AB { get; } = DatasusDataset.Monthly("SIASUS", "AB", "APAC de Acompanhamento a Cirurgia Bariátrica", Directory, 2008, 1);

        /// <summary>Gets SIASUS-ABO, <i>APAC Acompanhamento Pós Cirurgia Bariátrica</i>.</summary>
        public static DatasusDataset ABO { get; } = DatasusDataset.Monthly("SIASUS", "ABO", "APAC Acompanhamento Pós Cirurgia Bariátrica", Directory, 2008, 1);

        /// <summary>Gets SIASUS-AN, <i>APAC de Nefrologia</i>.</summary>
        public static DatasusDataset AN { get; } = DatasusDataset.Monthly("SIASUS", "AN", "APAC de Nefrologia", Directory, 2008, 1);

        /// <summary>Gets SIASUS-SAD, <i>Atenção Domiciliar</i>.</summary>
        public static DatasusDataset SAD { get; } = DatasusDataset.Monthly("SIASUS", "SAD", "Atenção Domiciliar", Directory, 2008, 1);

        /// <summary>Gets SIASUS-AMP, <i>Laudo de Acompanhamento Multiprofissional</i>.</summary>
        public static DatasusDataset AMP { get; } = DatasusDataset.Monthly("SIASUS", "AMP", "Laudo de Acompanhamento Multiprofissional", Directory, 2008, 1);
    }

    /// <summary>SIHSUS, the hospital information system (<i>Sistema de Informações Hospitalares do SUS</i>). Monthly files, one per state.</summary>
    public static class SihSus
    {
        private const string Directory = "/SIHSUS/200801_/Dados";
        private const string OlderDirectory = "/SIHSUS/199201_200712/Dados";

        /// <summary>Gets SIHSUS-RD, <i>AIH Reduzida</i>, from 1992-01 on; up to 2007-12 its files are in an older folder.</summary>
        public static DatasusDataset RD { get; } = DatasusDataset.Monthly("SIHSUS", "RD", "AIH Reduzida", Directory, 2008, 1, OlderDirectory, 1992, 1);

        /// <summary>
        /// Gets SIHSUS-SP, <i>Serviços Profissionais</i>; up to 2007-12 its files are in an older folder,
        /// which starts in 1992-01.
        /// </summary>
        public static DatasusDataset SP { get; } = DatasusDataset.Monthly("SIHSUS", "SP", "Serviços Profissionais", Directory, 2008, 1, OlderDirectory, 1992, 1);

        /// <summary>
        /// Gets SIHSUS-RJ, <i>AIH Rejeitadas</i>; up to 2007-12 its files are in an older folder, which
        /// starts in 1992-01.
        /// </summary>
        public static DatasusDataset RJ { get; } = DatasusDataset.Monthly("SIHSUS", "RJ", "AIH Rejeitadas", Directory, 2008, 1, OlderDirectory, 1992, 1);

        /// <summary>Gets SIHSUS-ER, <i>AIH Rejeitadas com código de erro</i>.</summary>
        public static DatasusDataset ER { get; } = DatasusDataset.Monthly("SIHSUS", "ER", "AIH Rejeitadas com código de erro", Directory, 2008, 1);
    }

    /// <summary>CNES, the registry of health facilities (<i>Cadastro Nacional de Estabelecimentos de Saúde</i>). Monthly files, one per state, each file type in a folder of its own.</summary>
    public static class Cnes
    {
        /// <summary>Gets CNES-LT, <i>Leitos</i>.</summary>
        public static DatasusDataset LT { get; } = Dataset("LT", "Leitos");

        /// <summary>Gets CNES-EQ, <i>Equipamentos</i>.</summary>
        public static DatasusDataset EQ { get; } = Dataset("EQ", "Equipamentos");

        /// <summary>Gets CNES-ST, <i>Estabelecimentos</i>.</summary>
        public static DatasusDataset ST { get; } = Dataset("ST", "Estabelecimentos");

        /// <summary>Gets CNES-PF, <i>Profissional</i>.</summary>
        public static DatasusDataset PF { get; } = Dataset("PF", "Profissional");

        /// <summary>Gets CNES-SR, <i>Serviço Especializado</i>.</summary>
        public static DatasusDataset SR { get; } = Dataset("SR", "Serviço Especializado");

        /// <summary>Gets CNES-HB, <i>Habilitação</i>.</summary>
        public static DatasusDataset HB { get; } = Dataset("HB", "Habilitação");

        /// <summary>Gets CNES-DC, <i>Dados Complementares</i>.</summary>
        public static DatasusDataset DC { get; } = Dataset("DC", "Dados Complementares");

        /// <summary>Gets CNES-EP, <i>Equipes</i>.</summary>
        public static DatasusDataset EP { get; } = Dataset("EP", "Equipes");

        /// <summary>Gets CNES-EF, <i>Estabelecimento Filantrópico</i>.</summary>
        public static DatasusDataset EF { get; } = Dataset("EF", "Estabelecimento Filantrópico");

        /// <summary>Gets CNES-GM, <i>Gestão e Metas</i>.</summary>
        public static DatasusDataset GM { get; } = Dataset("GM", "Gestão e Metas");

        /// <summary>Gets CNES-IN, <i>Incentivos</i>.</summary>
        public static DatasusDataset IN { get; } = Dataset("IN", "Incentivos");

        /// <summary>Gets CNES-RC, <i>Regra Contratual</i>.</summary>
        public static DatasusDataset RC { get; } = Dataset("RC", "Regra Contratual");

        /// <summary>Gets CNES-EE, <i>Estabelecimento de Ensino</i>.</summary>
        public static DatasusDataset EE { get; } = Dataset("EE", "Estabelecimento de Ensino");

        private static DatasusDataset Dataset(string code, string officialName) =>
            DatasusDataset.Monthly("CNES", code, officialName, "/CNES/200508_/Dados/" + code, 2005, 8);
    }

    /// <summary>SIM, the mortality information system (<i>Sistema de informações de Mortalidade</i>). Yearly files, one per state and one for the whole country.</summary>
    public static class Sim
    {
        /// <summary>
        /// Gets SIM-DO, <i>Declarações de Óbito</i>, from 1979 on; up to 1995 its files are in an older
        /// folder, with another name (<c>DOR{UF}{YY}.dbc</c>). The most recent years are published
        /// in a folder of preliminary data until DATASUS closes them.
        /// </summary>
        public static DatasusDataset DO { get; } = DatasusDataset.Yearly(
            "SIM", "DO", "Declarações de Óbito", DatasusCoverage.Uf | DatasusCoverage.National, "/SIM/CID10/DORES", "/SIM/PRELIM/DORES", 1996, "/SIM/CID9/DORES", "DOR", 2, 1979, "DORBR");
    }

    /// <summary>SINASC, the live births information system (<i>Sistema de informação de Nascidos Vivos</i>). Yearly files, one per state and one for the whole country.</summary>
    public static class Sinasc
    {
        /// <summary>
        /// Gets SINASC-DN, <i>Declarações de nascidos vivos</i>, from 1994 on; those of 1994 and 1995
        /// are in an older folder, with another name (<c>DNR{UF}{YYYY}.dbc</c>). The most recent years
        /// are published in a folder of preliminary data until DATASUS closes them.
        /// </summary>
        public static DatasusDataset DN { get; } = DatasusDataset.Yearly(
            "SINASC", "DN", "Declarações de nascidos vivos", DatasusCoverage.Uf | DatasusCoverage.National, "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", 1996, "/SINASC/1994_1995/Dados/DNRES", "DNR", 4, 1994, "DNBR");
    }

    /// <summary>CIHA (<i>Comunicação de Internação Hospitalar e Ambulatorial</i>). Monthly files, one per state.</summary>
    public static class Ciha
    {
        /// <summary>Gets CIHA-CIHA, <i>Comunicação de Internação Hospitalar e Ambulatorial</i>, from 2011-01 on.</summary>
        public static DatasusDataset CIHA { get; } = DatasusDataset.Monthly(
            "CIHA", "CIHA", "Comunicação de Internação Hospitalar e Ambulatorial", "/CIHA/201101_/Dados", 2011, 1);
    }

    /// <summary>
    /// CIH (<i>Sistema de Comunicação de Informação Hospitalar</i>). Monthly files, one per state.
    /// The last files listed in October 2026 are of 2011-04.
    /// </summary>
    public static class Cih
    {
        /// <summary>Gets CIH-CR, <i>Comunicação de Internação Hospitalar</i>, from 2008-01 on.</summary>
        public static DatasusDataset CR { get; } = DatasusDataset.Monthly("CIH", "CR", "Comunicação de Internação Hospitalar", "/CIH/200801_201012/Dados", 2008, 1);
    }

    /// <summary>
    /// SISPRENATAL (<i>Sistema de Monitoramento e Avaliação do Pré-Natal, Parto, Puerpério e Criança</i>).
    /// Monthly files, one per state. The last files listed in October 2026 are of 2014-12.
    /// </summary>
    public static class SisPrenatal
    {
        /// <summary>Gets SISPRENATAL-PN, <i>Pré-Natal</i>, from 2012-01 on.</summary>
        public static DatasusDataset PN { get; } = DatasusDataset.Monthly("SISPRENATAL", "PN", "Pré-Natal", "/SISPRENATAL/201201_/Dados", 2012, 1);
    }

    /// <summary>
    /// SISCOLO (<i>Sistema de Informações de Cânceres de Colo de Útero</i>). Monthly files, one per
    /// state, from 2006-01 on. The last files listed in October 2026 are of 2015-10.
    /// </summary>
    public static class SisColo
    {
        private const string Directory = "/SISCAN/SISCOLO4/Dados";

        /// <summary>Gets SISCOLO-CC, <i>Citopatológico de Colo de Útero</i>.</summary>
        public static DatasusDataset CC { get; } = DatasusDataset.Monthly("SISCOLO", "CC", "Citopatológico de Colo de Útero", Directory, 2006, 1);

        /// <summary>Gets SISCOLO-HC, <i>Histopatológico de Colo de Útero</i>.</summary>
        public static DatasusDataset HC { get; } = DatasusDataset.Monthly("SISCOLO", "HC", "Histopatológico de Colo de Útero", Directory, 2006, 1);
    }

    /// <summary>
    /// SISMAMA (<i>Sistema de Informações de Cânceres de Mama</i>). Monthly files, one per state,
    /// from 2009-01 on. The last files listed in October 2026 are of 2015-07.
    /// </summary>
    public static class SisMama
    {
        private const string Directory = "/SISCAN/SISMAMA/Dados";

        /// <summary>Gets SISMAMA-CM, <i>Citopatológico de Mama</i>.</summary>
        public static DatasusDataset CM { get; } = DatasusDataset.Monthly("SISMAMA", "CM", "Citopatológico de Mama", Directory, 2009, 1);

        /// <summary>Gets SISMAMA-HM, <i>Histopatológico de Mama</i>.</summary>
        public static DatasusDataset HM { get; } = DatasusDataset.Monthly("SISMAMA", "HM", "Histopatológico de Mama", Directory, 2009, 1);

        /// <summary>Gets SISMAMA-MM, <i>Exame Mamografia</i>.</summary>
        public static DatasusDataset MM { get; } = DatasusDataset.Monthly("SISMAMA", "MM", "Exame Mamografia", Directory, 2009, 1);
    }

    /// <summary>
    /// PCE (<i>Programa de Controle da Esquistossomose</i>). Yearly files, one per state and one for
    /// the whole country; the name carries two digits of the year.
    /// </summary>
    public static class Pce
    {
        /// <summary>Gets PCE-PCE, <i>Programa de Controle da Esquistossomose</i>, from 1995 on.</summary>
        public static DatasusDataset PCE { get; } = DatasusDataset.Yearly(
            "PCE", "PCE", "Programa de Controle da Esquistossomose", DatasusCoverage.Uf | DatasusCoverage.National, "/PCE/Dados", null, 1995, yearDigits: 2);
    }

    /// <summary>
    /// RESP (<i>Notificações de casos suspeitos de SCZ</i>). Yearly files, one per state and one for
    /// the whole country; the name carries two digits of the year.
    /// </summary>
    public static class Resp
    {
        /// <summary>Gets RESP-RESP, <i>Notificações de casos suspeitos de SCZ</i>, from 2015 on.</summary>
        public static DatasusDataset RESP { get; } = DatasusDataset.Yearly(
            "RESP", "RESP", "Notificações de casos suspeitos de SCZ", DatasusCoverage.Uf | DatasusCoverage.National, "/RESP/DADOS", null, 2015, yearDigits: 2);
    }
}
