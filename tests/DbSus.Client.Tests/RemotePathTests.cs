using Xunit;

namespace DbSus.Client.Tests;

public class RemotePathTests
{
    private const string Root = "/dissemin/publicos";

    [Theory]
    [InlineData("/SIASUS/200801_/Dados/PAMG2407a.dbc")]
    [InlineData("SIASUS/200801_/Dados/PAMG2407a.dbc")]
    [InlineData("/dissemin/publicos/SIASUS/200801_/Dados/PAMG2407a.dbc")]
    [InlineData("/DISSEMIN/PUBLICOS/SIASUS/200801_/Dados/PAMG2407a.dbc")]
    public void Parse_AcceptedForms_NormalizeToTheSamePath(string path)
    {
        var remote = RemotePath.Parse(path, Root);

        Assert.Equal("/SIASUS/200801_/Dados/PAMG2407a.dbc", remote.Relative);
        Assert.Equal("/dissemin/publicos/SIASUS/200801_/Dados/PAMG2407a.dbc", remote.Full);
        Assert.Equal("PAMG2407a.dbc", remote.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/dissemin/publicos")]
    public void Parse_Root_IsAcceptedOnlyForListings(string path)
    {
        Assert.Equal("/dissemin/publicos", RemotePath.Parse(path, Root, allowRoot: true).Full);
        Assert.Throws<ArgumentException>(() => RemotePath.Parse(path, Root));
    }

    [Theory]
    [InlineData("/SIASUS/../SIHSUS/x.dbc", "'.' and '..'")]
    [InlineData("/SIASUS/./x.dbc", "'.' and '..'")]
    [InlineData("/SIASUS//x.dbc", "empty segment")]
    [InlineData("ftp://ftp.datasus.gov.br/dissemin/publicos/x.dbc", "URLs")]
    [InlineData("/SIASUS\\x.dbc", "separator")]
    [InlineData("/SIASUS/x\u0001.dbc", "control characters")]
    [InlineData("/dissemin/restritos/x.dbc", "outside the root")]
    [InlineData("/SIASUS/a:b.dbc", "invalid in file names")]
    [InlineData("/SIASUS/x*.dbc", "invalid in file names")]
    [InlineData("/SIASUS/CON", "reserved device name")]
    [InlineData("/SIASUS/nul.dbc", "reserved device name")]
    [InlineData("/SIASUS/x.dbc.", "ends with a dot")]
    [InlineData("/SIASUS/ x.dbc", "starts or ends with a space")]
    public void Parse_UnsafeOrNonPortablePath_ThrowsArgumentExceptionWithTheReason(string path, string reason)
    {
        var exception = Assert.Throws<ArgumentException>(() => RemotePath.Parse(path, Root));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Siblings_AndParent_StayUnderTheRoot()
    {
        var remote = RemotePath.Parse("/SIASUS/200801_/Dados/PAMG2407a.dbc", Root);

        Assert.Equal("/SIASUS/200801_/Dados/PAMG2407b.dbc", remote.Sibling(Root, "PAMG2407b.dbc").Relative);
        Assert.Equal("/SIASUS/200801_/Dados", remote.Parent(Root).Relative);
    }
}
