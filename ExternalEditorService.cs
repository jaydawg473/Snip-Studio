using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace SnipStudio
{
    public static class ExternalEditorService
    {
        // A separate PNG is sent to the chosen editor. Neither the open document nor
        // an existing saved image is overwritten by an external editing session.
        public static string Open(Bitmap image, string executablePath)
        {
            if (image == null) throw new ArgumentNullException("image");
            string executable = ValidateExecutable(executablePath);
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(localData))
                throw new InvalidOperationException("The local working-copy folder is unavailable.");

            string workingCopy = SaveWorkingCopy(image, Path.Combine(localData, "SnipStudio", "ExternalEdits"));
            try
            {
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = executable;
                start.Arguments = QuoteArgument(workingCopy);
                start.WorkingDirectory = Path.GetDirectoryName(executable);
                start.UseShellExecute = false;
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                        throw new InvalidOperationException("Windows did not start the selected application.");
                }
                return workingCopy;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "The selected editing application could not be opened. Your snip is unchanged. " +
                    "Its working copy has been kept here:\r\n" + workingCopy + "\r\n\r\n" + error.Message, error);
            }
        }

        internal static string ValidateExecutable(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("Choose an editing application in Settings first.");
            string executable;
            try { executable = Path.GetFullPath(executablePath); }
            catch (Exception error)
            {
                throw new InvalidOperationException("Choose a valid application (.exe) in Settings.", error);
            }
            if (!String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new InvalidOperationException("The editing application was not found. Choose an existing application (.exe) in Settings.");
            return executable;
        }

        internal static string SaveWorkingCopy(Bitmap image, string directory)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentNullException("directory");
            Directory.CreateDirectory(directory);
            string name = "Snip " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".png";
            string destination = Path.Combine(directory, name);
            string pending = destination + ".pending";
            try
            {
                using (Bitmap snapshot = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb))
                using (FileStream stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    snapshot.Save(stream, ImageFormat.Png);
                    stream.Flush(true);
                }
                File.Move(pending, destination);
                return destination;
            }
            catch
            {
                // Only the incomplete file created by this invocation is removed.
                try { if (File.Exists(pending)) File.Delete(pending); } catch (Exception) { }
                throw;
            }
        }

        // CommandLineToArgvW / C runtime quoting. The executable itself is always
        // passed separately through ProcessStartInfo.FileName, never a shell.
        internal static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            StringBuilder quoted = new StringBuilder();
            quoted.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }
                quoted.Append('\\', backslashes);
                quoted.Append(character);
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
