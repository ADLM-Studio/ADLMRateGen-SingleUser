using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>
    /// Many client bills still arrive as old-format .xls workbooks, which the bill reader
    /// (EPPlus, .xlsx only) cannot open. Excel itself converts them: a hidden instance opens the
    /// .xls read-only and saves an .xlsx copy in RateGen's working folder. The client's file is
    /// never written to, and the priced copy RateGen saves is an .xlsx.
    /// </summary>
    public static class LegacyWorkbookConverter
    {
        private const int XlOpenXmlWorkbook = 51;          // XlFileFormat.xlOpenXMLWorkbook
        private const int MsoAutomationSecurityForceDisable = 3;

        public static bool IsLegacy(string path) =>
            string.Equals(Path.GetExtension(path), ".xls", StringComparison.OrdinalIgnoreCase);

        /// <summary>Where the .xlsx copy of <paramref name="xlsPath"/> is written.</summary>
        public static string WorkingCopyPath(string xlsPath)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ADLM", "RateGen", "Bills");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(xlsPath) + " " + stamp + ".xlsx");
        }

        /// <summary>Converts the .xls to an .xlsx copy and returns the copy's path.</summary>
        public static string ToXlsx(string xlsPath)
        {
            Type? excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null)
                throw new NotSupportedException(
                    "This is an old-format .xls workbook, and Excel is not installed here to convert it. " +
                    "Save it as .xlsx on a PC with Excel and import that copy.");

            string target = WorkingCopyPath(xlsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            dynamic? app = null;
            dynamic? book = null;
            bool ours = false;
            try
            {
                app = Activator.CreateInstance(excelType)!;
                // Excel normally starts a new, empty instance for automation. If it handed back
                // one the user already has open, close only our workbook and never quit it.
                ours = (int)app.Workbooks.Count == 0;
                app.DisplayAlerts = false;
                app.ScreenUpdating = false;
                try { app.AutomationSecurity = MsoAutomationSecurityForceDisable; } catch { }
                try { app.AskToUpdateLinks = false; } catch { }

                book = app.Workbooks.Open(xlsPath, 0, true);   // Filename, UpdateLinks: none, ReadOnly
                book.SaveAs(target, XlOpenXmlWorkbook);
                return target;
            }
            catch (COMException ex)
            {
                throw new NotSupportedException(
                    "This old-format .xls workbook could not be converted (" + ex.Message.Trim() + "). " +
                    "Open it in Excel, save it as .xlsx, and import that copy.", ex);
            }
            finally
            {
                if (book != null)
                {
                    try { book.Close(false); } catch { }
                    try { Marshal.FinalReleaseComObject(book); } catch { }
                }
                if (app != null)
                {
                    if (ours) { try { app.Quit(); } catch { } }
                    try { Marshal.FinalReleaseComObject(app); } catch { }
                }
            }
        }
    }
}
