using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public static class OfficeDocumentConverter
    {
        public static Task<string> ConvertToDocxAsync(string docPath)
        {
            return Task.Run(() =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "TempConversion");
                Directory.CreateDirectory(tempFile);
                string destPath = Path.Combine(tempFile, Guid.NewGuid().ToString("N") + ".docx");

                dynamic? wordApp = null;
                dynamic? document = null;
                try
                {
                    Type? wordType = Type.GetTypeFromProgID("Word.Application");
                    if (wordType == null)
                    {
                        throw new InvalidOperationException("Microsoft Word is not installed.");
                    }

                    wordApp = Activator.CreateInstance(wordType);
                    if (wordApp == null)
                    {
                        throw new InvalidOperationException("Could not create Word application instance.");
                    }

                    wordApp.Visible = false;
                    wordApp.DisplayAlerts = 0; // wdAlertsNone = 0

                    dynamic documents = wordApp.Documents;
                    document = documents.Open(docPath, ReadOnly: true, ConfirmConversions: false, AddToRecentFiles: false);
                    document.SaveAs2(destPath, 16); // 16 = wdFormatXMLDocument (docx)
                    document.Close(0); // 0 = wdDoNotSaveChanges
                    return destPath;
                }
                catch (Exception ex)
                {
                    if (File.Exists(destPath))
                    {
                        try { File.Delete(destPath); } catch { }
                    }
                    throw new InvalidOperationException($"Word conversion failed: {ex.Message}", ex);
                }
                finally
                {
                    if (document != null)
                    {
                        try { Marshal.ReleaseComObject(document); } catch { }
                    }
                    if (wordApp != null)
                    {
                        try { wordApp.Quit(0); } catch { } // 0 = wdDoNotSaveChanges
                        try { Marshal.ReleaseComObject(wordApp); } catch { }
                    }
                }
            });
        }

        public static Task<string> ConvertToXlsxAsync(string xlsPath)
        {
            return Task.Run(() =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "TempConversion");
                Directory.CreateDirectory(tempFile);
                string destPath = Path.Combine(tempFile, Guid.NewGuid().ToString("N") + ".xlsx");

                dynamic? excelApp = null;
                dynamic? workbook = null;
                try
                {
                    Type? excelType = Type.GetTypeFromProgID("Excel.Application");
                    if (excelType == null)
                    {
                        throw new InvalidOperationException("Microsoft Excel is not installed.");
                    }

                    excelApp = Activator.CreateInstance(excelType);
                    if (excelApp == null)
                    {
                        throw new InvalidOperationException("Could not create Excel application instance.");
                    }

                    excelApp.Visible = false;
                    excelApp.DisplayAlerts = false;

                    dynamic workbooks = excelApp.Workbooks;
                    workbook = workbooks.Open(xlsPath, ReadOnly: true, UpdateLinks: false);
                    workbook.SaveAs(destPath, 51); // 51 = xlOpenXMLWorkbook (xlsx)
                    workbook.Close(false); // false = do not save changes
                    return destPath;
                }
                catch (Exception ex)
                {
                    if (File.Exists(destPath))
                    {
                        try { File.Delete(destPath); } catch { }
                    }
                    throw new InvalidOperationException($"Excel conversion failed: {ex.Message}", ex);
                }
                finally
                {
                    if (workbook != null)
                    {
                        try { Marshal.ReleaseComObject(workbook); } catch { }
                    }
                    if (excelApp != null)
                    {
                        try { excelApp.Quit(); } catch { }
                        try { Marshal.ReleaseComObject(excelApp); } catch { }
                    }
                }
            });
        }

        public static Task<string> ConvertToPptxAsync(string pptPath)
        {
            return Task.Run(() =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "TempConversion");
                Directory.CreateDirectory(tempFile);
                string destPath = Path.Combine(tempFile, Guid.NewGuid().ToString("N") + ".pptx");

                dynamic? pptApp = null;
                dynamic? presentation = null;
                try
                {
                    Type? pptType = Type.GetTypeFromProgID("PowerPoint.Application");
                    if (pptType == null)
                    {
                        throw new InvalidOperationException("Microsoft PowerPoint is not installed.");
                    }

                    pptApp = Activator.CreateInstance(pptType);
                    if (pptApp == null)
                    {
                        throw new InvalidOperationException("Could not create PowerPoint application instance.");
                    }

                    pptApp.DisplayAlerts = 1; // ppAlertsNone = 1

                    dynamic presentations = pptApp.Presentations;
                    presentation = presentations.Open(pptPath, ReadOnly: -1, Untitled: 0, WithWindow: 0); // ReadOnly = -1 (msoTrue), WithWindow = 0 (msoFalse)
                    presentation.SaveAs(destPath, 24); // 24 = ppSaveAsOpenXMLPresentation (pptx)
                    presentation.Close();
                    return destPath;
                }
                catch (Exception ex)
                {
                    if (File.Exists(destPath))
                    {
                        try { File.Delete(destPath); } catch { }
                    }
                    throw new InvalidOperationException($"PowerPoint conversion failed: {ex.Message}", ex);
                }
                finally
                {
                    if (presentation != null)
                    {
                        try { Marshal.ReleaseComObject(presentation); } catch { }
                    }
                    if (pptApp != null)
                    {
                        try { pptApp.Quit(); } catch { }
                        try { Marshal.ReleaseComObject(pptApp); } catch { }
                    }
                }
            });
        }
    }
}
