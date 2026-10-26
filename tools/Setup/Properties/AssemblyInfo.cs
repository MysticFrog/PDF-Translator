using System.Reflection;
using System.Runtime.InteropServices;

// The installer shows this version before anything is installed - at that point
// the application's own executable is still inside it and cannot be asked.
[assembly: AssemblyTitle("PDF Translator installer")]
[assembly: AssemblyDescription("Installs PDF Translator")]
[assembly: AssemblyProduct("PDF Translator")]
[assembly: AssemblyCompany("Andrew Jones")]
[assembly: AssemblyCopyright("Copyright © 2026 Andrew Jones")]

[assembly: ComVisible(false)]

// AssemblyVersion is deliberately fixed: it is the identity the runtime binds
// against. The file version is generated into Properties\BuildVersion.cs by
// build.ps1 from the version in PdfTranslator.csproj (plus the commit count when the
// sources are in git). Without it the version reads 0.0.0.0, which is truthful.
[assembly: AssemblyVersion("1.0.0.0")]
