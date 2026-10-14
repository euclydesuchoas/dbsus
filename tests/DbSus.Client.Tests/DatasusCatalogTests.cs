using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

public class DatasusCatalogTests
{
    private const DatasusCoverage UfAndNational = DatasusCoverage.Uf | DatasusCoverage.National;

    // The whole catalog, one row per dataset: source, code, official name (as DATASUS spells it),
    // coverage, periodicity, first period accepted, and, of the folder layout in use now, the
    // folder, the folder of preliminary data and the name pattern.
    [Theory]
    [InlineData("SIASUS", "PA", "Produção Ambulatorial", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "1994-07", "/SIASUS/200801_/Dados", null, "PA{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AD", "APAC de Laudos Diversos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AD{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AM", "APAC de Medicamentos", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AM{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AQ", "APAC de Quimioterapia", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AQ{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AR", "APAC de Radioterapia", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AR{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "PS", "Psicossocial", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "PS{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "ATD", "APAC Tratamento Dialítico", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "ATD{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "ACF", "APAC Confeção de Fístula Arteriovenosa", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "ACF{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AB", "APAC de Acompanhamento a Cirurgia Bariátrica", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AB{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "ABO", "APAC Acompanhamento Pós Cirurgia Bariátrica", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "ABO{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AN", "APAC de Nefrologia", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AN{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "SAD", "Atenção Domiciliar", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "SAD{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "AMP", "Laudo de Acompanhamento Multiprofissional", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "AMP{UF}{YY}{MM}.dbc")]
    [InlineData("SIASUS", "BI", "Produção Ambulatorial Individualizado", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/SIASUS/200801_/Dados", null, "BI{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "RD", "AIH Reduzida", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "1992-01", "/SIHSUS/200801_/Dados", null, "RD{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "SP", "Serviços Profissionais", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "1992-01", "/SIHSUS/200801_/Dados", null, "SP{UF}{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "RJ", "AIH Rejeitadas", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "1992-01", "/SIHSUS/200801_/Dados", null, "RJ{UF}{YY}{MM}.dbc")]
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
    [InlineData("CNES", "EE", "Estabelecimento de Ensino", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2005-08", "/CNES/200508_/Dados/EE", null, "EE{UF}{YY}{MM}.dbc")]
    [InlineData("SIM", "DO", "Declarações de Óbito", UfAndNational, DatasusPeriodicity.Yearly, "1979", "/SIM/CID10/DORES", "/SIM/PRELIM/DORES", "DO{UF}{YYYY}.dbc")]
    [InlineData("SINASC", "DN", "Declarações de nascidos vivos", UfAndNational, DatasusPeriodicity.Yearly, "1994", "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", "DN{UF}{YYYY}.dbc")]
    [InlineData("CIHA", "CIHA", "Comunicação de Internação Hospitalar e Ambulatorial", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2011-01", "/CIHA/201101_/Dados", null, "CIHA{UF}{YY}{MM}.dbc")]
    [InlineData("CIH", "CR", "Comunicação de Internação Hospitalar", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2008-01", "/CIH/200801_201012/Dados", null, "CR{UF}{YY}{MM}.dbc")]
    [InlineData("SISPRENATAL", "PN", "Pré-Natal", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2012-01", "/SISPRENATAL/201201_/Dados", null, "PN{UF}{YY}{MM}.dbc")]
    [InlineData("SISCOLO", "CC", "Citopatológico de Colo de Útero", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2006-01", "/SISCAN/SISCOLO4/Dados", null, "CC{UF}{YY}{MM}.dbc")]
    [InlineData("SISCOLO", "HC", "Histopatológico de Colo de Útero", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2006-01", "/SISCAN/SISCOLO4/Dados", null, "HC{UF}{YY}{MM}.dbc")]
    [InlineData("SISMAMA", "CM", "Citopatológico de Mama", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2009-01", "/SISCAN/SISMAMA/Dados", null, "CM{UF}{YY}{MM}.dbc")]
    [InlineData("SISMAMA", "HM", "Histopatológico de Mama", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2009-01", "/SISCAN/SISMAMA/Dados", null, "HM{UF}{YY}{MM}.dbc")]
    [InlineData("SISMAMA", "MM", "Exame Mamografia", DatasusCoverage.Uf, DatasusPeriodicity.Monthly, "2009-01", "/SISCAN/SISMAMA/Dados", null, "MM{UF}{YY}{MM}.dbc")]
    [InlineData("PCE", "PCE", "Programa de Controle da Esquistossomose", UfAndNational, DatasusPeriodicity.Yearly, "1995", "/PCE/Dados", null, "PCE{UF}{YY}.dbc")]
    [InlineData("RESP", "RESP", "Notificações de casos suspeitos de SCZ", UfAndNational, DatasusPeriodicity.Yearly, "2015", "/RESP/DADOS", null, "RESP{UF}{YY}.dbc")]
    [InlineData("SIHSUS", "CH", null, DatasusCoverage.National, DatasusPeriodicity.Monthly, "2008-01", "/SIHSUS/200801_/Dados", null, "CHBR{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "CM", null, DatasusCoverage.National, DatasusPeriodicity.Monthly, "2008-01", "/SIHSUS/200801_/Dados", null, "CMBR{YY}{MM}.dbc")]
    [InlineData("SIHSUS", "MT", null, DatasusCoverage.National, DatasusPeriodicity.Monthly, "1995-01", "/SIHSUS/Arquivos_MTBR", null, "MTBR{YY}{MM}.dbc")]
    [InlineData("SIM", "DOEXT", "Declarações de Óbitos por causas externas", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1979", "/SIM/CID10/DOFET", "/SIM/PRELIM/DOFET", "DOEXT{YY}.dbc")]
    [InlineData("SIM", "DOFET", "Declarações de Óbitos fetais", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1979", "/SIM/CID10/DOFET", "/SIM/PRELIM/DOFET", "DOFET{YY}.dbc")]
    [InlineData("SIM", "DOINF", "Declarações de Óbitos infantis", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1979", "/SIM/CID10/DOFET", "/SIM/PRELIM/DOFET", "DOINF{YY}.dbc")]
    [InlineData("SIM", "DOMAT", "Declarações de Óbitos maternos", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1996", "/SIM/CID10/DOFET", "/SIM/PRELIM/DOFET", "DOMAT{YY}.dbc")]
    [InlineData("SIM", "DOREXT", "Mortalidade de residentes no exterior", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1996", "/SIM/CID10/DOFET", "/SIM/PRELIM/DOFET", "DOREXT{YY}.dbc")]
    [InlineData("SINASC", "DNEX", "Declarações de nascidos vivos residentes no exterior", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1996", "/SINASC/1996_/Dados/DNRES", "/SINASC/PRELIM/DNRES", "DNEX{YYYY}.dbc")]
    [InlineData("ESUSNOTIFICA", "DCCR", "Doença de Chagas Crônica", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2023", "/ESUSNOTIFICA/DADOS/FINAIS", "/ESUSNOTIFICA/DADOS/PRELIM", "DCCRBR{YY}.dbc")]
    [InlineData("PO", "PO", "Painel de Oncologia", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2013", "/painel_oncologia/Dados", null, "POBR{YYYY}.dbc")]
    [InlineData("SINAN", "ANIM", "Acidente por Animais Peçonhentos", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ANIMBR{YY}.dbc")]
    [InlineData("SINAN", "ANTR", "Atendimento Antirrabico", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ANTRBR{YY}.dbc")]
    [InlineData("SINAN", "AIDA", "AIDS em adultos", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "AIDABR{YY}.dbc")]
    [InlineData("SINAN", "AIDC", "AIDS em crianças", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "AIDCBR{YY}.dbc")]
    [InlineData("SINAN", "BOTU", "Botulismo", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "BOTUBR{YY}.dbc")]
    [InlineData("SINAN", "COLE", "Cólera", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "COLEBR{YY}.dbc")]
    [InlineData("SINAN", "COQU", "Coqueluche", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "COQUBR{YY}.dbc")]
    [InlineData("SINAN", "DENG", "Dengue", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2000", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "DENGBR{YY}.dbc")]
    [InlineData("SINAN", "DIFT", "Difteria", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "DIFTBR{YY}.dbc")]
    [InlineData("SINAN", "DCRJ", "Doença de Creutzfeldt-Jakob (DCJ)", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "DCRJBR{YY}.dbc")]
    [InlineData("SINAN", "CHAG", "Doença de Chagas Aguda", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2000", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "CHAGBR{YY}.dbc")]
    [InlineData("SINAN", "EXAN", "Doença exantemáticas", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "EXANBR{YY}.dbc")]
    [InlineData("SINAN", "ESQU", "Esquistossomose", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ESQUBR{YY}.dbc")]
    [InlineData("SINAN", "ESPO", "Esporotricose (Epizootia)", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2013", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ESPOBR{YY}.dbc")]
    [InlineData("SINAN", "CHIK", "Febre de Chikungunya", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2014", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "CHIKBR{YY}.dbc")]
    [InlineData("SINAN", "FMAC", "Febre Maculosa", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "FMACBR{YY}.dbc")]
    [InlineData("SINAN", "FTIF", "Febre Tifóide", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "FTIFBR{YY}.dbc")]
    [InlineData("SINAN", "HANS", "Hanseníase", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2001", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HANSBR{YY}.dbc")]
    [InlineData("SINAN", "HANT", "Hantavirose", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1999", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HANTBR{YY}.dbc")]
    [InlineData("SINAN", "HEPA", "Hepatites Virais", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HEPABR{YY}.dbc")]
    [InlineData("SINAN", "HIVA", "HIV em adultos", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HIVABR{YY}.dbc")]
    [InlineData("SINAN", "HIVC", "HIV em crianças", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HIVCBR{YY}.dbc")]
    [InlineData("SINAN", "HIVE", "HIV em crianças expostas", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2015", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HIVEBR{YY}.dbc")]
    [InlineData("SINAN", "HIVG", "HIV em gestante", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "HIVGBR{YY}.dbc")]
    [InlineData("SINAN", "INFL", "Influenza Pandêmica", DatasusCoverage.National, DatasusPeriodicity.Yearly, "1999", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "INFLBR{YY}.dbc")]
    [InlineData("SINAN", "IEXO", "Intoxicação Exógena", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "IEXOBR{YY}.dbc")]
    [InlineData("SINAN", "LEIV", "Leishmaniose Visceral", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2000", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "LEIVBR{YY}.dbc")]
    [InlineData("SINAN", "LTAN", "Leishmaniose Tegumentar Americana", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2000", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "LTANBR{YY}.dbc")]
    [InlineData("SINAN", "LEPT", "Leptospirose", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2000", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "LEPTBR{YY}.dbc")]
    [InlineData("SINAN", "MALA", "Malária", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2004", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "MALABR{YY}.dbc")]
    [InlineData("SINAN", "MENI", "Meningite", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "MENIBR{YY}.dbc")]
    [InlineData("SINAN", "PFAN", "Paralisia Flácida Aguda", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "PFANBR{YY}.dbc")]
    [InlineData("SINAN", "PEST", "Peste", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "PESTBR{YY}.dbc")]
    [InlineData("SINAN", "RAIV", "Raiva", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "RAIVBR{YY}.dbc")]
    [InlineData("SINAN", "ROTA", "Rotavírus", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2009", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ROTABR{YY}.dbc")]
    [InlineData("SINAN", "SIFA", "Sífilis Adquirida", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2010", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "SIFABR{YY}.dbc")]
    [InlineData("SINAN", "SIFC", "Sífilis Congênita", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "SIFCBR{YY}.dbc")]
    [InlineData("SINAN", "SIFG", "Sífilis em Gestante", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "SIFGBR{YY}.dbc")]
    [InlineData("SINAN", "SRC", "Síndrome da Rubéola Congênia", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "SRCBR{YY}.dbc")]
    [InlineData("SINAN", "SDTA", "Surto Doenças Transmitidas por Alimentos", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "SDTABR{YY}.dbc")]
    [InlineData("SINAN", "TETA", "Tétano Acidental", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "TETABR{YY}.dbc")]
    [InlineData("SINAN", "TETN", "Tétano Neonatal", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2014", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "TETNBR{YY}.dbc")]
    [InlineData("SINAN", "TOXC", "Toxoplasmose Congênita", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2019", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "TOXCBR{YY}.dbc")]
    [InlineData("SINAN", "TOXG", "Toxoplasmose Gestacional", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2019", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "TOXGBR{YY}.dbc")]
    [InlineData("SINAN", "NTRA", "Notificação de Tracoma", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2010", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "NTRABR{YY}.dbc")]
    [InlineData("SINAN", "TRAC", "Inquérito de Tracoma", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2009", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "TRACBR{YY}.dbc")]
    [InlineData("SINAN", "TUBE", "Tuberculose", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2001", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "TUBEBR{YY}.dbc")]
    [InlineData("SINAN", "VARC", "Varicela", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "VARCBR{YY}.dbc")]
    [InlineData("SINAN", "VIOL", "Violência doméstica, sexual e/ou outras violências", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2009", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "VIOLBR{YY}.dbc")]
    [InlineData("SINAN", "ZIKA", "Zika Vírus", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2015", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ZIKABR{YY}.dbc")]
    [InlineData("SINAN", "ACBI", "Acidente de trabalho com material biológico", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ACBIBR{YY}.dbc")]
    [InlineData("SINAN", "ACGR", "Acidente de trabalho", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "ACGRBR{YY}.dbc")]
    [InlineData("SINAN", "CANC", "Cancêr relacionado ao trabalho", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2007", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "CANCBR{YY}.dbc")]
    [InlineData("SINAN", "DERM", "Dermatoses ocupacionais", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "DERMBR{YY}.dbc")]
    [InlineData("SINAN", "LERD", "LER/Dort", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "LERDBR{YY}.dbc")]
    [InlineData("SINAN", "PAIR", "Perda auditiva por ruído relacionado ao trabalho", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "PAIRBR{YY}.dbc")]
    [InlineData("SINAN", "PNEU", "Pneumoconioses realacionadas ao trabalho", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "PNEUBR{YY}.dbc")]
    [InlineData("SINAN", "MENT", "Transtornos mentais relacionados ao trabalho", DatasusCoverage.National, DatasusPeriodicity.Yearly, "2006", "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", "MENTBR{YY}.dbc")]
    public void Dataset_IsAsTheCatalogTableSays(
        string source, string code, string? officialName, DatasusCoverage coverage, DatasusPeriodicity periodicity, string firstPeriod, string directory, string? preliminaryDirectory, string fileNamePattern)
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

        // The folder layout in use now is the last era, and has no end.
        var era = dataset.Eras[dataset.Eras.Count - 1];
        Assert.Same(era, dataset.CurrentEra);
        Assert.Null(era.LastPeriod);
        Assert.Equal(dataset.FirstPeriod, dataset.Eras[0].FirstPeriod);
        Assert.Equal(directory, era.Directory);
        Assert.Equal(preliminaryDirectory, era.PreliminaryDirectory);
        Assert.Equal(fileNamePattern, era.FileNamePattern);
    }

    [Fact]
    public void Coverage_None_IsTheDefault_AndNoDatasetHasIt()
    {
        Assert.Equal(0, (int)DatasusCoverage.None);
        Assert.Equal(DatasusCoverage.None, default(DatasusCoverage));
        Assert.Equal("None", default(DatasusCoverage).ToString());
        Assert.Equal("Uf, National", (DatasusCoverage.Uf | DatasusCoverage.National).ToString());
        Assert.All(DatasusCatalog.Datasets, dataset => Assert.NotEqual(DatasusCoverage.None, dataset.Coverage));
    }

    [Fact]
    public void Datasets_Are112_EachOnce_GroupedBySource_InTheOrderOfTheCatalog()
    {
        var datasets = DatasusCatalog.Datasets;

        Assert.Equal(112, datasets.Count);
        Assert.Equal(112, datasets.Select(dataset => dataset.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ["SIASUS", "SIHSUS", "CNES", "SIM", "SINASC", "CIHA", "CIH", "SISPRENATAL", "SISCOLO", "SISMAMA", "PCE", "RESP", "ESUSNOTIFICA", "PO", "SINAN"],
            datasets.Select(dataset => dataset.Source).Distinct());
        Assert.Equal(
            [
                "SIASUS: PA, AD, AM, AQ, AR, PS, ATD, ACF, AB, ABO, AN, SAD, AMP, BI",
                "SIHSUS: RD, SP, RJ, ER, CH, CM, MT",
                "CNES: LT, EQ, ST, PF, SR, HB, DC, EP, EF, GM, IN, RC, EE",
                "SIM: DO, DOEXT, DOFET, DOINF, DOMAT, DOREXT",
                "SINASC: DN, DNEX",
                "CIHA: CIHA",
                "CIH: CR",
                "SISPRENATAL: PN",
                "SISCOLO: CC, HC",
                "SISMAMA: CM, HM, MM",
                "PCE: PCE",
                "RESP: RESP",
                "ESUSNOTIFICA: DCCR",
                "PO: PO",
                "SINAN: ANIM, ANTR, AIDA, AIDC, BOTU, COLE, COQU, DENG, DIFT, DCRJ, CHAG, EXAN, ESQU, ESPO, CHIK, FMAC, FTIF, HANS, HANT, HEPA, HIVA, HIVC, HIVE, HIVG, INFL, IEXO, LEIV, LTAN, LEPT, MALA, MENI, PFAN, PEST, RAIV, ROTA, SIFA, SIFC, SIFG, SRC, SDTA, TETA, TETN, TOXC, TOXG, NTRA, TRAC, TUBE, VARC, VIOL, ZIKA, ACBI, ACGR, CANC, DERM, LERD, PAIR, PNEU, MENT",
            ],
            datasets.GroupBy(dataset => dataset.Source).Select(group => group.Key + ": " + string.Join(", ", group.Select(dataset => dataset.Code))));

        // 69 have one file for the whole country and none by state: every one of SINAN and eleven
        // of other sources. The other 43 have files by state, and four of these one for the whole country too.
        Assert.Equal(
            ["SIHSUS-CH", "SIHSUS-CM", "SIHSUS-MT", "SIM-DOEXT", "SIM-DOFET", "SIM-DOINF", "SIM-DOMAT", "SIM-DOREXT", "SINASC-DNEX", "ESUSNOTIFICA-DCCR", "PO-PO"],
            datasets.Where(dataset => dataset.Coverage == DatasusCoverage.National && dataset.Source != "SINAN").Select(dataset => dataset.ToString()));
        Assert.All(datasets.Where(dataset => dataset.Source == "SINAN"), dataset => Assert.Equal(DatasusCoverage.National, dataset.Coverage));
        Assert.Equal(58, datasets.Count(dataset => dataset.Source == "SINAN"));
        Assert.Equal(69, datasets.Count(dataset => dataset.Coverage == DatasusCoverage.National));
        Assert.Equal(43, datasets.Count(dataset => dataset.Coverage.HasFlag(DatasusCoverage.Uf)));
        Assert.Equal(39, datasets.Count(dataset => dataset.Coverage == DatasusCoverage.Uf));
        Assert.Equal(4, datasets.Count(dataset => dataset.Coverage == UfAndNational));

        // DATASUS publishes no name for three of them; every other one has a name.
        Assert.Equal(["SIHSUS-CH", "SIHSUS-CM", "SIHSUS-MT"], datasets.Where(dataset => dataset.OfficialName is null).Select(dataset => dataset.ToString()));
        Assert.All(datasets.Where(dataset => dataset.OfficialName is not null), dataset => Assert.False(string.IsNullOrWhiteSpace(dataset.OfficialName), dataset.ToString()));
    }

    [Fact]
    public void EveryProperty_IsInDatasets_UnderItsOwnCodeAndSource()
    {
        var groups = typeof(DatasusCatalog).GetNestedTypes(BindingFlags.Public);
        var properties = groups.SelectMany(group => group.GetProperties(BindingFlags.Public | BindingFlags.Static).Select(property => (Group: group, Property: property))).ToList();

        Assert.Equal(["Cih", "Ciha", "Cnes", "EsusNotifica", "Pce", "Po", "Resp", "SiaSus", "SihSus", "Sim", "Sinan", "Sinasc", "SisColo", "SisMama", "SisPrenatal"], groups.Select(group => group.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(112, properties.Count);
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

    // The texts that say how many file types the catalog has. CHANGELOG.md is left out on purpose:
    // the entry of a released version keeps its number when a later version adds file types.
    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/client.md")]
    [InlineData("docs/datasus-paths.md")]
    [InlineData("docs/decisions.md")]
    [InlineData("src/DbSus.Client/DatasusCatalog.cs")]
    [InlineData("src/DbSus.Client/DbSus.Client.csproj")]   // the description and the release notes of the package
    public void TextsThatCountTheFileTypes_GiveTheNumberOfDatasets(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryPaths.Root, file.Replace('/', Path.DirectorySeparatorChar)));
        var counts = Regex.Matches(text, @"\b(\d+) (?:DATASUS )?(?:file types\b|of SIASUS\b)")
            .Cast<Match>()
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(counts);   // each of these files states the number at least once
        Assert.All(counts, count => Assert.Equal(DatasusCatalog.Datasets.Count, count));
    }

    [Fact]
    public void PathsGuide_HasOneRowPerDataset_SayingWhatTheCatalogSays()
    {
        // Rows of the tables under "The file types in the catalog": seven cells, the first one a source.
        var guide = File.ReadAllLines(Path.Combine(RepositoryPaths.Root, "docs", "datasus-paths.md"));
        var sources = DatasusCatalog.Datasets.Select(dataset => dataset.Source).Distinct().ToList();
        var rows = guide
            .Select(line => line.Split('|').Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length == 9 && sources.Contains(cells[1]))
            .ToList();

        Assert.Equal(DatasusCatalog.Datasets.Count, rows.Count);
        foreach (var (cells, dataset) in rows.Zip(DatasusCatalog.Datasets, (cells, dataset) => (cells, dataset)))
        {
            Assert.Equal(dataset.Source, cells[1]);
            Assert.Equal(dataset.Code, cells[2]);
            Assert.Equal(dataset.OfficialName ?? "(none published)", cells[3]);
            Assert.Equal(dataset.Coverage == DatasusCoverage.Uf ? "state" : dataset.Coverage == DatasusCoverage.National ? "country" : "state and country", cells[4]);
            Assert.Equal(dataset.Periodicity == DatasusPeriodicity.Monthly ? "monthly" : "yearly", cells[5]);
            Assert.Equal(dataset.FirstPeriod.ToString(), cells[6]);

            // What was checked of the file type: a whole file read, a header alone, or only the names in a
            // listing; and, for the one file type of which the server had no file, that there was none.
            Assert.True(dataset.ToString() == "SINAN-INFL" ? cells[7] == "**no file**" : cells[7] is "read" or "header" or "names", $"{dataset}: \"{cells[7]}\"");

            // The folder and the file name are said once per table, in the line under the heading
            // above it; a source whose file types are in more than one folder has more than one heading.
            var era = dataset.CurrentEra;
            var row = Array.FindIndex(guide, line => line.StartsWith($"| {dataset.Source} | {dataset.Code} |", StringComparison.Ordinal));
            var heading = Array.FindLastIndex(guide, row, line => line.StartsWith("### ", StringComparison.Ordinal));
            Assert.True(heading >= 0 && guide[heading].StartsWith("### " + dataset.Source, StringComparison.Ordinal), dataset.ToString());
            var layout = guide[heading + 2];
            var folder = layout.Contains("{CODE}`,", StringComparison.Ordinal) ? era.Directory.Remove(era.Directory.Length - dataset.Code.Length) + "{CODE}" : era.Directory;
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

    // The end of both messages: a file type that the catalog does not have may still be on the
    // server, and the message says so without claiming that the file exists.
    private const string GetMessageEnd =
        "A file type that is not in the catalog may still be on the server: use its path as a string. "
        + "What the catalog leaves out is described in https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md";

    private static readonly string[] NotInTheMessageOfCnes = ["SIASUS", "SIHSUS", "SINASC", "RD, SP", "PA, AD", "DOEXT"];
    private static readonly string[] NotInTheMessageOfNoSource = ["LT, EQ", "RD, SP", "PA, AD", "DOEXT", "Of "];

    // A source of the catalog and a code it does not have: the message lists the codes of that
    // source, and of no other.
    [Theory]
    [InlineData("SIHSUS", "XX", "SIHSUS", "RD, SP, RJ, ER, CH, CM, MT")]
    [InlineData("sihsus", "xx", "SIHSUS", "RD, SP, RJ, ER, CH, CM, MT")]
    [InlineData("SIHSUS", " RD", "SIHSUS", "RD, SP, RJ, ER, CH, CM, MT")]
    [InlineData("SIASUS", "XX", "SIASUS", "PA, AD, AM, AQ, AR, PS, ATD, ACF, AB, ABO, AN, SAD, AMP, BI")]
    [InlineData("SISMAMA", "QQ", "SISMAMA", "CM, HM, MM")]                                               // a made-up code, which no source uses
    [InlineData("SISCOLO", "CM", "SISCOLO", "CC, HC")]                                                   // a code of another source
    [InlineData("SIM", "", "SIM", "DO, DOEXT, DOFET, DOINF, DOMAT, DOREXT")]
    [InlineData("CIH", "CIHA", "CIH", "CR")]                                                             // CIHA is another source
    [InlineData("PO", "POBR", "PO", "PO")]
    public void Get_CodeThatTheSourceDoesNotHave_ThrowsListingTheCodesOfThatSourceOnly(string source, string code, string sourceInTheCatalog, string codes)
    {
        Assert.False(DatasusCatalog.TryGet(source, code, out var missing));
        Assert.Null(missing);

        var exception = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get(source, code));

        Assert.Equal("code", exception.ParamName);
        Assert.StartsWith($"The catalog has no dataset '{source}-{code}'. Of {sourceInTheCatalog} it has: {codes}. {GetMessageEnd}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A source the catalog does not have: the message lists the sources, and no code.
    [Theory]
    [InlineData("SIA", "PA")]              // the source is SIASUS
    [InlineData("IBGE", "POP")]            // on the server, as .zip files
    [InlineData("SIHSUS-RD", "")]
    [InlineData("", "SIHSUS-RD")]
    [InlineData(" SIHSUS", "RD")]
    public void Get_SourceThatTheCatalogDoesNotHave_ThrowsListingTheSourcesOnly(string source, string code)
    {
        Assert.False(DatasusCatalog.TryGet(source, code, out var missing));
        Assert.Null(missing);

        var exception = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get(source, code));

        Assert.Equal("source", exception.ParamName);
        Assert.StartsWith(
            $"The catalog has no source '{source}'. It has: SIASUS, SIHSUS, CNES, SIM, SINASC, CIHA, CIH, SISPRENATAL, SISCOLO, SISMAMA, PCE, RESP, ESUSNOTIFICA, PO, SINAN. {GetMessageEnd}",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The message has what was asked for and nothing of the rest of the catalog.
    [Fact]
    public void Get_Message_HasNoCodeOfAnotherSource_AndIsShort()
    {
        var ofSource = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get("CNES", "XX")).Message;
        var noSource = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get("XX", "LT")).Message;

        Assert.Contains("Of CNES it has: LT, EQ, ST, PF, SR, HB, DC, EP, EF, GM, IN, RC, EE.", ofSource, StringComparison.Ordinal);
        Assert.All(NotInTheMessageOfCnes, other => Assert.DoesNotContain(other, ofSource, StringComparison.Ordinal));
        Assert.All(NotInTheMessageOfNoSource, code => Assert.DoesNotContain(code, noSource, StringComparison.Ordinal));
        Assert.InRange(ofSource.Length, 1, 420);
        Assert.InRange(noSource.Length, 1, 520);
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
