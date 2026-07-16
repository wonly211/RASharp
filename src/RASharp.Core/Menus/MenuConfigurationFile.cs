using System.Text;

namespace RASharp.Core.Menus;

public sealed class MenuConfigurationFile
{
    private MenuConfigurationFile(
        string path,
        Encoding encoding,
        bool exists,
        MenuEditorDocument document)
    {
        Path = path;
        Encoding = encoding;
        Exists = exists;
        Document = document;
    }

    public string Path { get; }

    public Encoding Encoding { get; }

    public bool Exists { get; private set; }

    public MenuEditorDocument Document { get; }

    public static MenuConfigurationFile Load(string path, int menuNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new MenuConfigurationFile(
                path,
                new UTF8Encoding(false),
                false,
                MenuEditorDocument.Parse(string.Empty, path, menuNumber));
        }

        var (text, encoding) = ReadText(path);
        return new MenuConfigurationFile(
            path,
            encoding,
            true,
            MenuEditorDocument.Parse(text, path, menuNumber));
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporaryPath = Path + ".tmp";
        File.WriteAllText(temporaryPath, Document.Serialize(), Encoding);
        File.Move(temporaryPath, Path, overwrite: true);
        Exists = true;
        Document.AcceptChanges();
    }

    private static (string Text, Encoding Encoding) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return (Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length)), new UTF8Encoding(true));
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return (Encoding.Unicode.GetString(bytes.AsSpan(Encoding.Unicode.Preamble.Length)), Encoding.Unicode);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return (Encoding.BigEndianUnicode.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.Preamble.Length)), Encoding.BigEndianUnicode);
        }

        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return (utf8.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var gbk = Encoding.GetEncoding(936);
            return (gbk.GetString(bytes), gbk);
        }
    }
}
