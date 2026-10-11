using System.Reflection;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

public class DatasusCatalogTests
{
    private const DatasusCoverage UfAndNational = DatasusCoverage.Uf | DatasusCoverage.National;

    // The whole catalog, one row per dataset: source, code, official name (as DATASUS spells it),
    // coverage, periodicity, first period accepted, folder, folder of preliminary data, name pattern.
    [Theory]
    [InlineData("SIASUS", "PA", "Produção Ambulatorial", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "PA{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AD", "APAC de Laudos Diversos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AD{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AM", "APAC de Medicamentos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AM{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AQ", "APAC de Quimioterapia", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AQ{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AR", "APAC de Radioterapia", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AR{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "PS", "Psicossocial", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "PS{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "ATD", "APAC Tratamento Dialítico", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "ATD{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "ACF", "APAC Confeção de Fístula Arteriovenosa", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "ACF{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "RD", "AIH Reduzida", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIHSUS/200801_/Dados", null, "RD{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "SP", "Serviços Profissionais", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIHSUS/200801_/Dados", null, "SP{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "RJ", "AIH Rejeitadas", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIHSUS/200801_/Dados", null, "RJ{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "ER", "AIH Rejeitadas com código de erro", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIHSUS/200801_/Dados", null, "ER{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "LT", "Leitos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/LT", null, "LT{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "EQ", "Equipamentos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/EQ", null, "EQ{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "ST", "Estabelecimentos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/ST", null, "ST{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "PF", "Profissional", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/PF", null, "PF{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "SR", "Serviço Especializado", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/SR", null, "SR{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "HB", "Habilitação", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/HB", null, "HB{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "DC", "Dados Complementares", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/DC", null, "DC{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "EP", "Equipes", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/EP", null, "EP{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "EF", "Estabelecimento Filantrópico", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/EF", null, "EF{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "GM", "Gestão e Metas", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/GM", null, "GM{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "IN", "Incentivos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/IN", null, "IN{UF}{YY}{MM}.dbc")]
    [InlineData("CNES", "RC", "Regra Contratual", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/RC", null, "RC{UF}{YY}{MM}.dbc")]
    [InlineData("SIM", "DO", "Declarações de Óbito", UfAndNational, DatasusPeriodicity.Yearly, "1996", "/SIM/CID10/DORES", "/SIM/PRELIM/DORES", "DO{UF}{YYYY}.dbc")]
    [InlineData("SINASC", "DN", "Declarações de nascidos vivos", UfAndNational, DatasusPeriodicity.Yearly, "1996", "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", "DN{UF}{YYYY}.dbc")]
    public void Dataset_IsAsTheCatalogTableSays(
        string source, string code, string officialName, DatasusCoverage coverage, DatasusPeriodicity periodicity, string firstPeriod, string directory, string? preliminaryDirectory, string fileNamePattern)
    {
        var dataset = DatasusCatalog.Get(source, code);

        Assert.Equal(source, dataset.Source);
        Assert.Equal(code, dataset.Code);
        Assert.Equal(officialName, dataset.OfficialName);
        Assert.Equal(coverage, dataset.Coverage);
        Assert.Equal(periodicity, dataset.Periodicity);
        Assert.Equal(firstPeriod, dataset.FirstPeriod.ToString());
        Assert.Equal(periodicity, dataset.FirstPeriod.Periodicity);
        Assert.Equal(source + "-" + code, dataset.ToString());
        Assert.Contains(dataset, DatasusCatalog.Datasets);

        // This version describes only the folder layout in use now.
        var era = Assert.Single(dataset.Eras);
        Assert.Same(era, dataset.CurrentEra);
        Assert.Null(era.LastPeriod);
        Assert.Equal(directory, era.Directory);
        Assert.Equal(preliminaryDirectory, era.PreliminaryDirectory);
        Assert.Equal(fileNamePattern, era.FileNamePattern);
    }

    [Fact]
    public void Datasets_Are26_EachOnce_GroupedBySource()
    {
        var datasets = DatasusCatalog.Datasets;

        Assert.Equal(26, datasets.Count);
        Assert.Equal(26, datasets.Select(dataset => dataset.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["SIASUS", "SIHSUS", "CNES", "SIM", "SINASC"], datasets.Select(dataset => dataset.Source).Distinct());
        Assert.Equal(
            [8, 4, 12, 1, 1],
            datasets.GroupBy(dataset => dataset.Source).Select(group => group.Count()));
        Assert.All(datasets, dataset => Assert.True(dataset.Coverage.HasFlag(DatasusCoverage.Uf), dataset.ToString()));
        Assert.All(datasets, dataset => Assert.False(string.IsNullOrWhiteSpace(dataset.OfficialName), dataset.ToString()));
    }

    [Fact]
    public void EveryProperty_IsInDatasets_UnderItsOwnCodeAndSource()
    {
        var groups = typeof(DatasusCatalog).GetNestedTypes(BindingFlags.Public);
        var properties = groups.SelectMany(group => group.GetProperties(BindingFlags.Public | BindingFlags.Static).Select(property => (Group: group, Property: property))).ToList();

        Assert.Equal(["Cnes", "SiaSus", "SihSus", "Sim", "Sinasc"], groups.Select(group => group.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(26, properties.Count);
        foreach (var (group, property) in properties)
        {
            var dataset = Assert.IsType<DatasusDataset>(property.GetValue(null));

            Assert.Equal(property.Name, dataset.Code);
            Assert.Equal(group.Name, dataset.Source, ignoreCase: true);
            Assert.Same(dataset, property.GetValue(null));
            Assert.Same(dataset, DatasusCatalog.Get(dataset.Source, dataset.Code));
            Assert.Contains(dataset, DatasusCatalog.Datasets);
        }
    }

    [Fact]
    public void PathsGuide_HasOneRowPerDataset_SayingWhatTheCatalogSays()
    {
        // Rows of the tables under "The file types in the catalog": six cells, the first one a source.
        var guide = File.ReadAllLines(Path.Combine(RepositoryPaths.Root, "docs", "datasus-paths.md"));
        var sources = DatasusCatalog.Datasets.Select(dataset => dataset.Source).Distinct().ToList();
        var rows = guide
            .Select(line => line.Split('|').Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length == 8 && sources.Contains(cells[1]))
            .ToList();

        Assert.Equal(DatasusCatalog.Datasets.Count, rows.Count);
        foreach (var (cells, dataset) in rows.Zip(DatasusCatalog.Datasets, (cells, dataset) => (cells, dataset)))
        {
            Assert.Equal(dataset.Source, cells[1]);
            Assert.Equal(dataset.Code, cells[2]);
            Assert.Equal(dataset.OfficialName, cells[3]);
            Assert.Equal(dataset.Coverage == DatasusCoverage.Uf ? "state" : "state and country", cells[4]);
            Assert.Equal(dataset.Periodicity == DatasusPeriodicity.Monthly ? "monthly" : "yearly", cells[5]);
            Assert.Equal(dataset.FirstPeriod.ToString(), cells[6]);

            // The folder and the file name are said once per source, in the line under its heading.
            var era = dataset.CurrentEra;
            var heading = Array.IndexOf(guide, "### " + dataset.Source);
            Assert.True(heading >= 0, dataset.Source);
            var layout = guide[heading + 2];
            var folder = era.Directory.EndsWith("/" + dataset.Code, StringComparison.Ordinal) ? era.Directory.Remove(era.Directory.Length - dataset.Code.Length) + "{CODE}" : era.Directory;
            Assert.Contains($"`{folder}`", layout, StringComparison.Ordinal);
            if (era.PreliminaryDirectory is not null)
            {
                Assert.Contains($"`{era.PreliminaryDirectory}` for preliminary years", layout, StringComparison.Ordinal);
            }

            var pattern = layout.Substring(layout.LastIndexOf("file name `", StringComparison.Ordinal) + "file name `".Length).TrimEnd('.', '`');
            Assert.Equal(era.FileNamePattern, pattern.Replace("{CODE}", dataset.Code));
        }
    }

    [Theory]
    [InlineData("SIHSUS", "RD")]
    [InlineData("sihsus", "rd")]
    [InlineData("SihSus", "Rd")]
    public void Get_IgnoresCase(string source, string code)
    {
        Assert.Same(DatasusCatalog.SihSus.RD, DatasusCatalog.Get(source, code));
        Assert.True(DatasusCatalog.TryGet(source, code, out var dataset));
        Assert.Same(DatasusCatalog.SihSus.RD, dataset);
    }

    [Theory]
    [InlineData("SIHSUS", "XX")]
    [InlineData("SIA", "PA")]              // the source is SIASUS
    [InlineData("SIASUS", "BI")]           // on the server, but not in the catalog
    [InlineData("SIASUS", "AMP")]
    [InlineData("SINAN", "DENG")]          // files for the whole country only
    [InlineData("SIHSUS-RD", "")]
    [InlineData("", "SIHSUS-RD")]
    [InlineData("SIHSUS", " RD")]
    public void Get_DatasetOutsideTheCatalog_ThrowsListingTheOnesItHas(string source, string code)
    {
        Assert.False(DatasusCatalog.TryGet(source, code, out var missing));
        Assert.Null(missing);

        var exception = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get(source, code));

        Assert.Contains($"no dataset '{source}-{code}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SIASUS: PA, AD, AM, AQ, AR, PS, ATD, ACF", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SIHSUS: RD, SP, RJ, ER", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CNES: LT, EQ, ST, PF, SR, HB, DC, EP, EF, GM, IN, RC", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SIM: DO", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SINASC: DN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("path as a string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DatasusCatalog.Get(null!, "RD"));
        Assert.Throws<ArgumentNullException>(() => DatasusCatalog.Get("SIHSUS", null!));
        Assert.Throws<ArgumentNullException>(() => DatasusCatalog.TryGet(null!, "RD", out _));
        Assert.Throws<ArgumentNullException>(() => DatasusCatalog.TryGet("SIHSUS", null!, out _));
    }
}
