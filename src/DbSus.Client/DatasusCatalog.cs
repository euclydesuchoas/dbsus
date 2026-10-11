using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client;

/// <summary>
/// A partial catalog of the file types DATASUS lists: 26 of SIASUS, SIHSUS, CNES, SIM and SINASC,
/// by state, each as a <see cref="DatasusDataset"/>. Use a property, e.g.
/// <c>DatasusCatalog.SihSus.RD</c>, or look one up by text with <see cref="Get"/>.
/// </summary>
/// <remarks>
/// <para>
/// Most of the file types DATASUS lists are not here: those of other sources (SINAN, for one) and
/// other file types of these five sources. Nor are the files for the whole country, those of SIM
/// and SINASC included, or the periods in older folder layouts. A file type that is missing here
/// may still be on the server.
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
        SihSus.RD, SihSus.SP, SihSus.RJ, SihSus.ER,
        Cnes.LT, Cnes.EQ, Cnes.ST, Cnes.PF, Cnes.SR, Cnes.HB, Cnes.DC, Cnes.EP, Cnes.EF, Cnes.GM, Cnes.IN, Cnes.RC,
        Sim.DO,
        Sinasc.DN,
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

        /// <summary>Gets SIASUS-PA, <i>Produção Ambulatorial</i>. Split into lettered parts in the larger states.</summary>
        public static DatasusDataset PA { get; } = DatasusDataset.Monthly("SIASUS", "PA", "Produção Ambulatorial", Directory, 2008, 1);

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
    }

    /// <summary>SIHSUS, the hospital information system (<i>Sistema de Informações Hospitalares do SUS</i>). Monthly files, one per state.</summary>
    public static class SihSus
    {
        private const string Directory = "/SIHSUS/200801_/Dados";

        /// <summary>Gets SIHSUS-RD, <i>AIH Reduzida</i>.</summary>
        public static DatasusDataset RD { get; } = DatasusDataset.Monthly("SIHSUS", "RD", "AIH Reduzida", Directory, 2008, 1);

        /// <summary>Gets SIHSUS-SP, <i>Serviços Profissionais</i>.</summary>
        public static DatasusDataset SP { get; } = DatasusDataset.Monthly("SIHSUS", "SP", "Serviços Profissionais", Directory, 2008, 1);

        /// <summary>Gets SIHSUS-RJ, <i>AIH Rejeitadas</i>.</summary>
        public static DatasusDataset RJ { get; } = DatasusDataset.Monthly("SIHSUS", "RJ", "AIH Rejeitadas", Directory, 2008, 1);

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

        private static DatasusDataset Dataset(string code, string officialName) =>
            DatasusDataset.Monthly("CNES", code, officialName, "/CNES/200508_/Dados/" + code, 2005, 8);
    }

    /// <summary>SIM, the mortality information system (<i>Sistema de informações de Mortalidade</i>). Yearly files.</summary>
    public static class Sim
    {
        /// <summary>
        /// Gets SIM-DO, <i>Declarações de Óbito</i>, from 1996 on. The most recent years are published
        /// in a folder of preliminary data until DATASUS closes them.
        /// </summary>
        public static DatasusDataset DO { get; } = DatasusDataset.Yearly(
            "SIM", "DO", "Declarações de Óbito", DatasusCoverage.Uf | DatasusCoverage.National, "/SIM/CID10/DORES", "/SIM/PRELIM/DORES", 1996);
    }

    /// <summary>SINASC, the live births information system (<i>Sistema de informação de Nascidos Vivos</i>). Yearly files.</summary>
    public static class Sinasc
    {
        /// <summary>
        /// Gets SINASC-DN, <i>Declarações de nascidos vivos</i>, from 1996 on. The most recent years
        /// are published in a folder of preliminary data until DATASUS closes them.
        /// </summary>
        public static DatasusDataset DN { get; } = DatasusDataset.Yearly(
            "SINASC", "DN", "Declarações de nascidos vivos", DatasusCoverage.Uf | DatasusCoverage.National, "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", 1996);
    }
}
