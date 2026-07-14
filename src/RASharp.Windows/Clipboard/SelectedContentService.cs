using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using RASharp.Windows.Input;

namespace RASharp.Windows.Clipboard;

public sealed partial class SelectedContentService(Dispatcher dispatcher)
{
    public Task<SelectedContent> CaptureAsync(TimeSpan? timeout = null) =>
        dispatcher.InvokeAsync(() => CaptureOnDispatcherAsync(timeout ?? TimeSpan.FromMilliseconds(250)))
            .Task
            .Unwrap();

    private static async Task<SelectedContent> CaptureOnDispatcherAsync(TimeSpan timeout)
    {
        var backup = CloneClipboard();
        try
        {
            System.Windows.Clipboard.Clear();
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            KeyboardInput.SendCopy();

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20).ConfigureAwait(true);
                if (NativeMethods.GetClipboardSequenceNumber() != sequence)
                {
                    break;
                }
            }

            var files = ReadFiles();
            var text = ReadText();
            return new SelectedContent(text, files);
        }
        catch (COMException)
        {
            return SelectedContent.Empty;
        }
        finally
        {
            if (backup is not null)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(backup, true);
                }
                catch (COMException)
                {
                    // The target application may still own the clipboard. Selection capture remains valid.
                }
            }
        }
    }

    private static DataObject? CloneClipboard()
    {
        try
        {
            var source = System.Windows.Clipboard.GetDataObject();
            if (source is null)
            {
                return null;
            }

            var clone = new DataObject();
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    var data = source.GetData(format, autoConvert: false);
                    if (data is not null)
                    {
                        clone.SetData(format, data);
                    }
                }
                catch (COMException)
                {
                    // Some owner-rendered clipboard formats cannot be copied safely.
                }
            }

            return clone;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string[] ReadFiles()
    {
        if (!System.Windows.Clipboard.ContainsFileDropList())
        {
            return [];
        }

        StringCollection files = System.Windows.Clipboard.GetFileDropList();
        return files.Cast<string>().ToArray();
    }

    private static string ReadText() =>
        System.Windows.Clipboard.ContainsText()
            ? System.Windows.Clipboard.GetText()
            : string.Empty;

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial uint GetClipboardSequenceNumber();
    }
}
