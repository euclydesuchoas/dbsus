using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client;

/// <summary>
/// A partial catalog of the file types DATASUS lists: 111 of SIASUS, SIHSUS, CNES, SIM, SINASC,
/// CIHA, CIH, SISPRENATAL, SISCOLO, SISMAMA, PCE, RESP, ESUSNOTIFICA, PO and SINAN, each as a
/// <see cref="DatasusDataset"/>. Use a property, e.g. <c>DatasusCatalog.SihSus.RD</c>, or look one
/// up by text with <see cref="Get"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not everything on the DATASUS server is here: the files that are not <c>.dbc</c> files, for
/// one, are not. A file type that is missing here may still be on the server.
/// </para>
/// <para>
/// Of the file types here, 43 have one file per state, and four of these also have a file for
/// the whole country (SIM-DO, SINASC-DN, PCE and RESP); the other 68, the 57 of SINAN among
/// them, have a file for the whole country only (see <see cref="DatasusDataset.Coverage"/> and
/// <see cref="DatasusDataset.GetNationalPath"/>).
/// </para>
/// <para>
/// Nine of the file types also have the folder layout that came before the one in use now:
/// SIASUS-PA from 1994-07, SIHSUS RD, SP and RJ from 1992-01, SIM DO, DOEXT, DOFET and DOINF from
/// 1979 and SINASC-DN from 1994.
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
        SiaSus.AB, SiaSus.ABO, SiaSus.AN, SiaSus.SAD, SiaSus.AMP, SiaSus.BI,
        SihSus.RD, SihSus.SP, SihSus.RJ, SihSus.ER, SihSus.CH, SihSus.CM, SihSus.MT,
        Cnes.LT, Cnes.EQ, Cnes.ST, Cnes.PF, Cnes.SR, Cnes.HB, Cnes.DC, Cnes.EP, Cnes.EF, Cnes.GM, Cnes.IN, Cnes.RC, Cnes.EE,
        Sim.DO, Sim.DOEXT, Sim.DOFET, Sim.DOINF, Sim.DOMAT, Sim.DOREXT,
        Sinasc.DN, Sinasc.DNEX,
        Ciha.CIHA,
        Cih.CR,
        SisPrenatal.PN,
        SisColo.CC, SisColo.HC,
        SisMama.CM, SisMama.HM, SisMama.MM,
        Pce.PCE,
        Resp.RESP,
        EsusNotifica.DCCR,
        Po.PO,
        Sinan.ANIM, Sinan.ANTR, Sinan.AIDA, Sinan.AIDC, Sinan.BOTU, Sinan.COLE, Sinan.COQU, Sinan.DENG, Sinan.DIFT, Sinan.DCRJ,
        Sinan.CHAG, Sinan.EXAN, Sinan.ESQU, Sinan.ESPO, Sinan.CHIK, Sinan.FMAC, Sinan.FTIF, Sinan.HANS, Sinan.HANT, Sinan.HEPA,
        Sinan.HIVA, Sinan.HIVC, Sinan.HIVE, Sinan.HIVG, Sinan.IEXO, Sinan.LEIV, Sinan.LTAN, Sinan.LEPT, Sinan.MALA, Sinan.MENI,
        Sinan.PFAN, Sinan.PEST, Sinan.RAIV, Sinan.ROTA, Sinan.SIFA, Sinan.SIFC, Sinan.SIFG, Sinan.SRC, Sinan.SDTA, Sinan.TETA,
        Sinan.TETN, Sinan.TOXC, Sinan.TOXG, Sinan.NTRA, Sinan.TRAC, Sinan.TUBE, Sinan.VARC, Sinan.VIOL, Sinan.ZIKA, Sinan.ACBI,
        Sinan.ACGR, Sinan.CANC, Sinan.DERM, Sinan.LERD, Sinan.PAIR, Sinan.PNEU, Sinan.MENT,
    ];

    // Declared after Datasets: static fields are initialized in the order they are written.
    private static readonly Dictionary<string, DatasusDataset> ByKey = BuildIndex();

    /// <summary>Gets a dataset by its source and code, ignoring case, e.g. <c>Get("SIHSUS", "RD")</c>.</summary>
    /// <param name="source">The source, e.g. <c>SIHSUS</c>.</param>
    /// <param name="code">The code of the file type, e.g. <c>RD</c>.</param>
    /// <returns>The dataset.</returns>
    /// <exception cref="ArgumentException">
    /// The catalog has no such dataset. When it has the source, the message lists the file types it
    /// has of that source; when it does not, the sources it has. Either way it says how other files
    /// are reached.
    /// </exception>
    public static DatasusDataset Get(string source, string code)
    {
        if (TryGet(source, code, out var dataset))
        {
            return dataset;
        }

        // The message has what was asked for and no more: the codes of one source, or the sources.
        const string Partial = "The catalog is partial. A file type that is not in it may still be on the server: use its path as a string. What the catalog leaves out is described in ";
        var ofSource = Datasets.Where(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ofSource.Count == 0)
        {
            var sources = string.Join(", ", Datasets.Select(item => item.Source).Distinct());
            throw new ArgumentException($"The catalog has no source '{source}'. It has: {sources}. {Partial}{DatasusDataset.PathsGuide}", nameof(source));
        }

        var codes = string.Join(", ", ofSource.Select(item => item.Code));
        throw new ArgumentException($"The catalog has no dataset '{source}-{code}'. Of {ofSource[0].Source} it has: {codes}. {Partial}{DatasusDataset.PathsGuide}", nameof(code));
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

    /// <summary>
    /// SIASUS, the outpatient information system (<i>Sistema de Informações Ambulatoriais do SUS</i>).
    /// Monthly files, one per state; in the larger states the table of SIASUS-PA is split into
    /// lettered parts, and the one of SIASUS-BI into numbered parts.
    /// </summary>
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

        /// <summary>
        /// Gets SIASUS-BI, <i>Produção Ambulatorial Individualizado</i>. Split into numbered parts in
        /// the larger states (<c>BIMG2312_1.dbc</c>, <c>BIMG2312_2.dbc</c>): the client reads the
        /// numbered parts as the table and leaves aside the file without a number of the same state
        /// and month, when the server has both.
        /// </summary>
        public static DatasusDataset BI { get; } = DatasusDataset.MonthlyInNumberedParts("SIASUS", "BI", "Produção Ambulatorial Individualizado", Directory, 2008, 1);
    }

    /// <summary>
    /// SIHSUS, the hospital information system (<i>Sistema de Informações Hospitalares do SUS</i>).
    /// Monthly files, one per state; CH, CM and MT have one file for the whole country instead.
    /// </summary>
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

        /// <summary>Gets SIHSUS-CH. Monthly, for the whole country only. DATASUS publishes no name for this file type.</summary>
        public static DatasusDataset CH { get; } = DatasusDataset.CountryOnly("SIHSUS", "CH", null, DatasusPeriod.OfMonth(2008, 1), Directory, null, "CHBR", 2);

        /// <summary>Gets SIHSUS-CM. Monthly, for the whole country only. DATASUS publishes no name for this file type.</summary>
        public static DatasusDataset CM { get; } = DatasusDataset.CountryOnly("SIHSUS", "CM", null, DatasusPeriod.OfMonth(2008, 1), Directory, null, "CMBR", 2);

        /// <summary>
        /// Gets SIHSUS-MT. Monthly, for the whole country only, in a folder of its own; the files
        /// listed in October 2026 go from 1995-01 to 1997-05. DATASUS publishes no name for this file type.
        /// </summary>
        public static DatasusDataset MT { get; } = DatasusDataset.CountryOnly("SIHSUS", "MT", null, DatasusPeriod.OfMonth(1995, 1), "/SIHSUS/Arquivos_MTBR", null, "MTBR", 2);
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

    /// <summary>
    /// SIM, the mortality information system (<i>Sistema de informações de Mortalidade</i>). Yearly
    /// files: SIM-DO has one per state and one for the whole country, and the other file types one
    /// for the whole country only.
    /// </summary>
    public static class Sim
    {
        private const string CountryDirectory = "/SIM/CID10/DOFET";
        private const string CountryPreliminaryDirectory = "/SIM/PRELIM/DOFET";
        private const string CountryOlderDirectory = "/SIM/CID9/DOFET";

        /// <summary>
        /// Gets SIM-DO, <i>Declarações de Óbito</i>, from 1979 on; up to 1995 its files are in an older
        /// folder, with another name (<c>DOR{UF}{YY}.dbc</c>). The most recent years are published
        /// in a folder of preliminary data until DATASUS closes them.
        /// </summary>
        public static DatasusDataset DO { get; } = DatasusDataset.Yearly(
            "SIM", "DO", "Declarações de Óbito", DatasusCoverage.Uf | DatasusCoverage.National, "/SIM/CID10/DORES", "/SIM/PRELIM/DORES", 1996, "/SIM/CID9/DORES", "DOR", 2, 1979, "DORBR");

        /// <summary>
        /// Gets SIM-DOEXT, <i>Declarações de Óbitos por causas externas</i>, from 1979 on; up to 1995 its
        /// files are in an older folder. Yearly, for the whole country only.
        /// </summary>
        public static DatasusDataset DOEXT { get; } = DatasusDataset.CountryOnly(
            "SIM", "DOEXT", "Declarações de Óbitos por causas externas", 1996, CountryDirectory, CountryPreliminaryDirectory, "DOEXT", 2, CountryOlderDirectory, 1979);

        /// <summary>
        /// Gets SIM-DOFET, <i>Declarações de Óbitos fetais</i>, from 1979 on; up to 1995 its files are in an
        /// older folder. Yearly, for the whole country only.
        /// </summary>
        public static DatasusDataset DOFET { get; } = DatasusDataset.CountryOnly(
            "SIM", "DOFET", "Declarações de Óbitos fetais", 1996, CountryDirectory, CountryPreliminaryDirectory, "DOFET", 2, CountryOlderDirectory, 1979);

        /// <summary>
        /// Gets SIM-DOINF, <i>Declarações de Óbitos infantis</i>, from 1979 on; up to 1995 its files are in
        /// an older folder. Yearly, for the whole country only.
        /// </summary>
        public static DatasusDataset DOINF { get; } = DatasusDataset.CountryOnly(
            "SIM", "DOINF", "Declarações de Óbitos infantis", 1996, CountryDirectory, CountryPreliminaryDirectory, "DOINF", 2, CountryOlderDirectory, 1979);

        /// <summary>Gets SIM-DOMAT, <i>Declarações de Óbitos maternos</i>, from 1996 on. Yearly, for the whole country only.</summary>
        public static DatasusDataset DOMAT { get; } = DatasusDataset.CountryOnly(
            "SIM", "DOMAT", "Declarações de Óbitos maternos", DatasusPeriod.OfYear(1996), CountryDirectory, CountryPreliminaryDirectory, "DOMAT", 2);

        /// <summary>
        /// Gets SIM-DOREXT, <i>Mortalidade de residentes no exterior</i>. Yearly, for the whole country
        /// only. Its folder starts in 1996; the files listed in October 2026 start in 2013.
        /// </summary>
        public static DatasusDataset DOREXT { get; } = DatasusDataset.CountryOnly(
            "SIM", "DOREXT", "Mortalidade de residentes no exterior", DatasusPeriod.OfYear(1996), CountryDirectory, CountryPreliminaryDirectory, "DOREXT", 2);
    }

    /// <summary>
    /// SINASC, the live births information system (<i>Sistema de informação de Nascidos Vivos</i>).
    /// Yearly files: SINASC-DN has one per state and one for the whole country, and SINASC-DNEX one
    /// for the whole country only.
    /// </summary>
    public static class Sinasc
    {
        /// <summary>
        /// Gets SINASC-DN, <i>Declarações de nascidos vivos</i>, from 1994 on; those of 1994 and 1995
        /// are in an older folder, with another name (<c>DNR{UF}{YYYY}.dbc</c>). The most recent years
        /// are published in a folder of preliminary data until DATASUS closes them.
        /// </summary>
        public static DatasusDataset DN { get; } = DatasusDataset.Yearly(
            "SINASC", "DN", "Declarações de nascidos vivos", DatasusCoverage.Uf | DatasusCoverage.National, "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", 1996, "/SINASC/1994_1995/Dados/DNRES", "DNR", 4, 1994, "DNBR");

        /// <summary>
        /// Gets SINASC-DNEX, <i>Declarações de nascidos vivos residentes no exterior</i>. Yearly, for the
        /// whole country only. Its folder starts in 1996; the files listed in October 2026 start in 2014.
        /// </summary>
        public static DatasusDataset DNEX { get; } = DatasusDataset.CountryOnly(
            "SINASC", "DNEX", "Declarações de nascidos vivos residentes no exterior", DatasusPeriod.OfYear(1996), "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", "DNEX", 4);
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

    /// <summary>
    /// e-SUS Notifica. Yearly files for the whole country; the most recent years are published in
    /// a folder of preliminary data.
    /// </summary>
    public static class EsusNotifica
    {
        /// <summary>Gets ESUSNOTIFICA-DCCR, <i>Doença de Chagas Crônica</i>, from 2023 on.</summary>
        public static DatasusDataset DCCR { get; } = DatasusDataset.CountryOnly(
            "ESUSNOTIFICA", "DCCR", "Doença de Chagas Crônica", DatasusPeriod.OfYear(2023), "/ESUSNOTIFICA/DADOS/FINAIS", "/ESUSNOTIFICA/DADOS/PRELIM", "DCCRBR", 2);
    }

    /// <summary>PO (<i>Painel de Oncologia</i>). Yearly files for the whole country.</summary>
    public static class Po
    {
        /// <summary>Gets PO-PO, <i>Painel de Oncologia</i>, from 2013 on.</summary>
        public static DatasusDataset PO { get; } = DatasusDataset.CountryOnly("PO", "PO", "Painel de Oncologia", DatasusPeriod.OfYear(2013), "/painel_oncologia/Dados", null, "POBR", 4);
    }

    /// <summary>
    /// SINAN (<i>Sistema de Informação de Agravos de Notificação</i>). Yearly files for the whole
    /// country, one file type per notifiable condition; the most recent years are published in a
    /// folder of preliminary data.
    /// </summary>
    /// <remarks>
    /// The year in which a notifiable condition passes from the folder of preliminary data to the
    /// one of final data differs from one condition to another, and some are in one of the two
    /// folders only. The first period of each file type is the year of its first file in either
    /// folder, as the server listed them in October 2026.
    /// </remarks>
    public static class Sinan
    {
        /// <summary>Gets SINAN-ANIM, <i>Acidente por Animais Peçonhentos</i>.</summary>
        public static DatasusDataset ANIM { get; } = Of("ANIM", "Acidente por Animais Peçonhentos", 2007);

        /// <summary>Gets SINAN-ANTR, <i>Atendimento Antirrabico</i>.</summary>
        public static DatasusDataset ANTR { get; } = Of("ANTR", "Atendimento Antirrabico", 2006);

        /// <summary>Gets SINAN-AIDA, <i>AIDS em adultos</i>.</summary>
        public static DatasusDataset AIDA { get; } = Of("AIDA", "AIDS em adultos", 2007);

        /// <summary>Gets SINAN-AIDC, <i>AIDS em crianças</i>.</summary>
        public static DatasusDataset AIDC { get; } = Of("AIDC", "AIDS em crianças", 2007);

        /// <summary>Gets SINAN-BOTU, <i>Botulismo</i>.</summary>
        public static DatasusDataset BOTU { get; } = Of("BOTU", "Botulismo", 2007);

        /// <summary>Gets SINAN-COLE, <i>Cólera</i>.</summary>
        public static DatasusDataset COLE { get; } = Of("COLE", "Cólera", 2007);

        /// <summary>Gets SINAN-COQU, <i>Coqueluche</i>.</summary>
        public static DatasusDataset COQU { get; } = Of("COQU", "Coqueluche", 2007);

        /// <summary>Gets SINAN-DENG, <i>Dengue</i>.</summary>
        public static DatasusDataset DENG { get; } = Of("DENG", "Dengue", 2000);

        /// <summary>Gets SINAN-DIFT, <i>Difteria</i>.</summary>
        public static DatasusDataset DIFT { get; } = Of("DIFT", "Difteria", 2007);

        /// <summary>Gets SINAN-DCRJ, <i>Doença de Creutzfeldt-Jakob (DCJ)</i>.</summary>
        public static DatasusDataset DCRJ { get; } = Of("DCRJ", "Doença de Creutzfeldt-Jakob (DCJ)", 2007);

        /// <summary>Gets SINAN-CHAG, <i>Doença de Chagas Aguda</i>.</summary>
        public static DatasusDataset CHAG { get; } = Of("CHAG", "Doença de Chagas Aguda", 2000);

        /// <summary>Gets SINAN-EXAN, <i>Doença exantemáticas</i>.</summary>
        public static DatasusDataset EXAN { get; } = Of("EXAN", "Doença exantemáticas", 2007);

        /// <summary>Gets SINAN-ESQU, <i>Esquistossomose</i>.</summary>
        public static DatasusDataset ESQU { get; } = Of("ESQU", "Esquistossomose", 2007);

        /// <summary>Gets SINAN-ESPO, <i>Esporotricose (Epizootia)</i>.</summary>
        public static DatasusDataset ESPO { get; } = Of("ESPO", "Esporotricose (Epizootia)", 2013);

        /// <summary>Gets SINAN-CHIK, <i>Febre de Chikungunya</i>.</summary>
        public static DatasusDataset CHIK { get; } = Of("CHIK", "Febre de Chikungunya", 2014);

        /// <summary>Gets SINAN-FMAC, <i>Febre Maculosa</i>.</summary>
        public static DatasusDataset FMAC { get; } = Of("FMAC", "Febre Maculosa", 2007);

        /// <summary>Gets SINAN-FTIF, <i>Febre Tifóide</i>.</summary>
        public static DatasusDataset FTIF { get; } = Of("FTIF", "Febre Tifóide", 2007);

        /// <summary>Gets SINAN-HANS, <i>Hanseníase</i>.</summary>
        public static DatasusDataset HANS { get; } = Of("HANS", "Hanseníase", 2001);

        /// <summary>Gets SINAN-HANT, <i>Hantavirose</i>.</summary>
        public static DatasusDataset HANT { get; } = Of("HANT", "Hantavirose", 1999);

        /// <summary>Gets SINAN-HEPA, <i>Hepatites Virais</i>.</summary>
        public static DatasusDataset HEPA { get; } = Of("HEPA", "Hepatites Virais", 2007);

        /// <summary>Gets SINAN-HIVA, <i>HIV em adultos</i>.</summary>
        public static DatasusDataset HIVA { get; } = Of("HIVA", "HIV em adultos", 2007);

        /// <summary>Gets SINAN-HIVC, <i>HIV em crianças</i>.</summary>
        public static DatasusDataset HIVC { get; } = Of("HIVC", "HIV em crianças", 2007);

        /// <summary>Gets SINAN-HIVE, <i>HIV em crianças expostas</i>.</summary>
        public static DatasusDataset HIVE { get; } = Of("HIVE", "HIV em crianças expostas", 2015);

        /// <summary>Gets SINAN-HIVG, <i>HIV em gestante</i>.</summary>
        public static DatasusDataset HIVG { get; } = Of("HIVG", "HIV em gestante", 2007);

        /// <summary>Gets SINAN-IEXO, <i>Intoxicação Exógena</i>.</summary>
        public static DatasusDataset IEXO { get; } = Of("IEXO", "Intoxicação Exógena", 2006);

        /// <summary>Gets SINAN-LEIV, <i>Leishmaniose Visceral</i>.</summary>
        public static DatasusDataset LEIV { get; } = Of("LEIV", "Leishmaniose Visceral", 2000);

        /// <summary>Gets SINAN-LTAN, <i>Leishmaniose Tegumentar Americana</i>.</summary>
        public static DatasusDataset LTAN { get; } = Of("LTAN", "Leishmaniose Tegumentar Americana", 2000);

        /// <summary>Gets SINAN-LEPT, <i>Leptospirose</i>.</summary>
        public static DatasusDataset LEPT { get; } = Of("LEPT", "Leptospirose", 2000);

        /// <summary>Gets SINAN-MALA, <i>Malária</i>.</summary>
        public static DatasusDataset MALA { get; } = Of("MALA", "Malária", 2004);

        /// <summary>Gets SINAN-MENI, <i>Meningite</i>.</summary>
        public static DatasusDataset MENI { get; } = Of("MENI", "Meningite", 2007);

        /// <summary>Gets SINAN-PFAN, <i>Paralisia Flácida Aguda</i>.</summary>
        public static DatasusDataset PFAN { get; } = Of("PFAN", "Paralisia Flácida Aguda", 2007);

        /// <summary>Gets SINAN-PEST, <i>Peste</i>.</summary>
        public static DatasusDataset PEST { get; } = Of("PEST", "Peste", 2007);

        /// <summary>Gets SINAN-RAIV, <i>Raiva</i>.</summary>
        public static DatasusDataset RAIV { get; } = Of("RAIV", "Raiva", 2007);

        /// <summary>Gets SINAN-ROTA, <i>Rotavírus</i>.</summary>
        public static DatasusDataset ROTA { get; } = Of("ROTA", "Rotavírus", 2009);

        /// <summary>Gets SINAN-SIFA, <i>Sífilis Adquirida</i>.</summary>
        public static DatasusDataset SIFA { get; } = Of("SIFA", "Sífilis Adquirida", 2010);

        /// <summary>Gets SINAN-SIFC, <i>Sífilis Congênita</i>.</summary>
        public static DatasusDataset SIFC { get; } = Of("SIFC", "Sífilis Congênita", 2007);

        /// <summary>Gets SINAN-SIFG, <i>Sífilis em Gestante</i>.</summary>
        public static DatasusDataset SIFG { get; } = Of("SIFG", "Sífilis em Gestante", 2007);

        /// <summary>Gets SINAN-SRC, <i>Síndrome da Rubéola Congênia</i>.</summary>
        public static DatasusDataset SRC { get; } = Of("SRC", "Síndrome da Rubéola Congênia", 2007);

        /// <summary>Gets SINAN-SDTA, <i>Surto Doenças Transmitidas por Alimentos</i>.</summary>
        public static DatasusDataset SDTA { get; } = Of("SDTA", "Surto Doenças Transmitidas por Alimentos", 2007);

        /// <summary>Gets SINAN-TETA, <i>Tétano Acidental</i>.</summary>
        public static DatasusDataset TETA { get; } = Of("TETA", "Tétano Acidental", 2007);

        /// <summary>Gets SINAN-TETN, <i>Tétano Neonatal</i>.</summary>
        public static DatasusDataset TETN { get; } = Of("TETN", "Tétano Neonatal", 2014);

        /// <summary>Gets SINAN-TOXC, <i>Toxoplasmose Congênita</i>.</summary>
        public static DatasusDataset TOXC { get; } = Of("TOXC", "Toxoplasmose Congênita", 2019);

        /// <summary>Gets SINAN-TOXG, <i>Toxoplasmose Gestacional</i>.</summary>
        public static DatasusDataset TOXG { get; } = Of("TOXG", "Toxoplasmose Gestacional", 2019);

        /// <summary>Gets SINAN-NTRA, <i>Notificação de Tracoma</i>.</summary>
        public static DatasusDataset NTRA { get; } = Of("NTRA", "Notificação de Tracoma", 2010);

        /// <summary>Gets SINAN-TRAC, <i>Inquérito de Tracoma</i>.</summary>
        public static DatasusDataset TRAC { get; } = Of("TRAC", "Inquérito de Tracoma", 2009);

        /// <summary>Gets SINAN-TUBE, <i>Tuberculose</i>.</summary>
        public static DatasusDataset TUBE { get; } = Of("TUBE", "Tuberculose", 2001);

        /// <summary>Gets SINAN-VARC, <i>Varicela</i>.</summary>
        public static DatasusDataset VARC { get; } = Of("VARC", "Varicela", 2007);

        /// <summary>Gets SINAN-VIOL, <i>Violência doméstica, sexual e/ou outras violências</i>.</summary>
        public static DatasusDataset VIOL { get; } = Of("VIOL", "Violência doméstica, sexual e/ou outras violências", 2009);

        /// <summary>Gets SINAN-ZIKA, <i>Zika Vírus</i>.</summary>
        public static DatasusDataset ZIKA { get; } = Of("ZIKA", "Zika Vírus", 2015);

        /// <summary>Gets SINAN-ACBI, <i>Acidente de trabalho com material biológico</i>.</summary>
        public static DatasusDataset ACBI { get; } = Of("ACBI", "Acidente de trabalho com material biológico", 2006);

        /// <summary>Gets SINAN-ACGR, <i>Acidente de trabalho</i>.</summary>
        public static DatasusDataset ACGR { get; } = Of("ACGR", "Acidente de trabalho", 2006);

        /// <summary>Gets SINAN-CANC, <i>Cancêr relacionado ao trabalho</i>.</summary>
        public static DatasusDataset CANC { get; } = Of("CANC", "Cancêr relacionado ao trabalho", 2007);

        /// <summary>Gets SINAN-DERM, <i>Dermatoses ocupacionais</i>.</summary>
        public static DatasusDataset DERM { get; } = Of("DERM", "Dermatoses ocupacionais", 2006);

        /// <summary>Gets SINAN-LERD, <i>LER/Dort</i>.</summary>
        public static DatasusDataset LERD { get; } = Of("LERD", "LER/Dort", 2006);

        /// <summary>Gets SINAN-PAIR, <i>Perda auditiva por ruído relacionado ao trabalho</i>.</summary>
        public static DatasusDataset PAIR { get; } = Of("PAIR", "Perda auditiva por ruído relacionado ao trabalho", 2006);

        /// <summary>Gets SINAN-PNEU, <i>Pneumoconioses realacionadas ao trabalho</i>.</summary>
        public static DatasusDataset PNEU { get; } = Of("PNEU", "Pneumoconioses realacionadas ao trabalho", 2006);

        /// <summary>Gets SINAN-MENT, <i>Transtornos mentais relacionados ao trabalho</i>.</summary>
        public static DatasusDataset MENT { get; } = Of("MENT", "Transtornos mentais relacionados ao trabalho", 2006);

        private static DatasusDataset Of(string code, string officialName, int firstYear) =>
            DatasusDataset.CountryOnly("SINAN", code, officialName, DatasusPeriod.OfYear(firstYear), "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", code + "BR", 2);
    }
}
