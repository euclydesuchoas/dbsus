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
    [InlineData("/SIASUS/COM0.dbc", "reserved device name")]
    [InlineData("/SIASUS/lpt0", "reserved device name")]
    [InlineData("/SIASUS/COM¹.dbc", "reserved device name")]
    [InlineData("/SIASUS/COM²", "reserved device name")]
    [InlineData("/SIASUS/COM³.dbc", "reserved device name")]
    [InlineData("/SIASUS/LPT¹.dbc", "reserved device name")]
    [InlineData("/SIASUS/lpt²", "reserved device name")]
    [InlineData("/SIASUS/LPT³.dbc", "reserved device name")]
    [InlineData("/SIASUS/CONIN$", "reserved device name")]
    [InlineData("/SIASUS/conout$.dbc", "reserved device name")]
    [InlineData("/SIASUS/CON .dbc", "reserved device name")]
    [InlineData("/SIASUS/aux  .x.dbc", "reserved device name")]
    [InlineData("/SIASUS/x.dbc.", "ends with a dot")]
    [InlineData("/SIASUS/ x.dbc", "starts or ends with a space")]
    public void Parse_UnsafeOrNonPortablePath_ThrowsArgumentExceptionWithTheReason(string path, string reason)
    {
        var exception = Assert.Throws<ArgumentException>(() => RemotePath.Parse(path, Root));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    // Characters that the FTP library refuses in a path, or that hide in a name or break a line:
    // refused before anything is sent, and shown escaped in the message.
    [Theory]
    [InlineData("/SIASUS/x\u0085.dbc", "control characters", "\\u0085")]
    [InlineData("/SIASUS/x\u202E.dbc", "invisible format", "\\u202E")]
    [InlineData("/SIASUS/x\u200B.dbc", "invisible format", "\\u200B")]
    [InlineData("/SIASUS/x\uFEFF.dbc", "invisible format", "\\uFEFF")]
    [InlineData("/SIASUS/x\u2028.dbc", "line separator", "\\u2028")]
    [InlineData("/SIASUS/x\u2029.dbc", "line separator", "\\u2029")]
    [InlineData("/SIASUS/x\U000E0001.dbc", "invisible format", "\\uE0001")]
    [InlineData("/SIASUS/x%41.dbc", "'%' and ';'", "x%41.dbc")]
    [InlineData("/SIASUS/x;y.dbc", "'%' and ';'", "x;y.dbc")]
    public void Parse_CharactersTheFtpCommandCannotCarry_AreRefusedAndShownEscaped(string path, string reason, string shown)
    {
        var exception = Assert.Throws<ArgumentException>(() => RemotePath.Parse(path, Root));

        // On .NET Framework the message ends with a line that names the parameter.
        var message = exception.Message.Split([Environment.NewLine], StringSplitOptions.None)[0];
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.Contains(shown, message, StringComparison.Ordinal);
        Assert.DoesNotContain(message, c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dissemin/publicos")]
    [InlineData("/dissemin/publicos/")]
    [InlineData("/dados abertos")]
    public void RootProblem_AcceptableRoots_HaveNone(string root)
    {
        Assert.Null(RemotePath.RootProblem(root));
    }

    [Theory]
    [InlineData("", "must start with '/'")]
    [InlineData("dissemin/publicos", "must start with '/'")]
    [InlineData("/dissemin/publicos\r\nDELE x.dbc", "control characters (U+000D)")]
    [InlineData("/dissemin/publicos\u0000", "control characters (U+0000)")]
    [InlineData("/dissemin/\u202Epublicos", "invisible format")]
    [InlineData("/dissemin//publicos", "empty segment")]
    [InlineData("/dissemin/../restritos", "'.' and '..'")]
    [InlineData("/dissemin/publicos;", "'%' and ';'")]
    [InlineData("/dissemin\\publicos", "separator")]
    [InlineData("ftp://ftp.datasus.gov.br/dissemin", "must start with '/'")]
    [InlineData("/x/ftp://y", "URLs")]
    [InlineData("/dissemin/a:b", "invalid in file names")]
    [InlineData("/dissemin/publicos.", "ends with a dot")]
    public void RootProblem_UnacceptableRoots_SayWhy(string root, string reason)
    {
        var problem = RemotePath.RootProblem(root);

        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
        Assert.DoesNotContain(problem, c => c is '\r' or '\n' or '\0');
    }

    [Theory]
    [InlineData("/SIASUS/COM10.dbc")]
    [InlineData("/SIASUS/CONSOLE.dbc")]
    [InlineData("/SIASUS/LPT´.dbc")]
    [InlineData("/SIASUS/CONIN.dbc")]
    public void Parse_NamesThatOnlyLookLikeDevices_AreAccepted(string path)
    {
        Assert.Equal(path, RemotePath.Parse(path, Root).Relative);
    }

    [Fact]
    public void Siblings_AndParent_StayUnderTheRoot()
    {
        var remote = RemotePath.Parse("/SIASUS/200801_/Dados/PAMG2407a.dbc", Root);

        Assert.Equal("/SIASUS/200801_/Dados/PAMG2407b.dbc", remote.Sibling(Root, "PAMG2407b.dbc").Relative);
        Assert.Equal("/SIASUS/200801_/Dados", remote.Parent(Root).Relative);
    }
}
