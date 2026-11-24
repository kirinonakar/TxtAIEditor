using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

string appPath = Path.GetFullPath(args[0]);
string samples = args[1];
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);
Type viewerType = app.GetType("TxtAIEditor.Core.Services.OfficeDocumentViewerService", true)!;
Func<string, string, string> localize = (_, fallback) => fallback;
object viewer = Activator.CreateInstance(viewerType, localize)!;
MethodInfo render = viewerType.GetMethod("BuildHtmlAsync")!;
MethodInfo print = viewerType.GetMethod("BuildPdfHtmlAsync")!;
int passed = 0;
int failed = 0;
var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx" };
var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Word6.doc", "ppt_with_png_encrypted.ppt", "encrypted.xls", "truncated.doc", "truncated.ppt", "truncated.xls"
};
foreach (string file in Directory.GetFiles(samples).Where(file => supported.Contains(Path.GetExtension(file))))
{
    string name = Path.GetFileName(file);
    try
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(file));
        string html = await (Task<string>)render.Invoke(viewer, [file])!;
        try
        {
            string pdf = await (Task<string>)print.Invoke(viewer, [file])!;
            if (rejected.Contains(name) || !html.Contains("__txtAiEditorOfficeFind") || !html.Contains(name) ||
                pdf.Contains("__txtAiEditorOfficeFind") || !pdf.Contains(name))
                throw new InvalidOperationException("Unexpected viewer or print result.");
            File.WriteAllText(file + ".html", html);
            File.WriteAllText(file + ".pdf.html", pdf);
        }
        catch (Exception ex) when (rejected.Contains(name) && ex is IOException or InvalidDataException)
        {
            if (!html.Contains("__txtAiEditorOfficeFind")) throw;
        }
        if (!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)))) throw new IOException("The sample changed.");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.GetBaseException()}");
        failed++;
    }
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 && passed > 0 ? 0 : 1;
