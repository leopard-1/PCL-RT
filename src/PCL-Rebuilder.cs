// =============================================================================
//  PCL-Rebuilder
//  Removes the "Minecraft download unavailable" gate from an official
//  Plain Craft Launcher binary, without recompiling the launcher and without
//  touching Microsoft login / licence / authorisation logic.
//
//  Patch sites (all inside the Minecraft download module):
//    P1  PCL.ModModpack::ModpackInstall(string,string,string)
//        `ldstr <restriction>; newobj Exception; throw`  ->  NOP NOP NOP
//    P2  PCL.PageDownloadInstall::LoaderInit
//        LoaderTask("关闭下载", Sub() Throw ...)  ->  ModDownload.DlClientListLoader
//    P3  PCL.PageDownloadModify  (CardMinecraft visibility Collapsed -> Visible)
//
//  Everything else is left byte-for-byte as dnlib re-emits it.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

internal static class PCLRebuilder
{
    // "\u4E3A\u9075\u5B88\u76F8\u5173\u6CD5\u5F8B\u6CD5\u89C4" == "为遵守相关法律法规"
    private const string RestrictMarker = "\u4E3A\u9075\u5B88\u76F8\u5173\u6CD5\u5F8B\u6CD5\u89C4";
    // "\u5173\u95ED\u4E0B\u8F7D" == "关闭下载" (the dummy loader name used by the gate)
    private const string TaskNameMarker = "\u5173\u95ED\u4E0B\u8F7D";
    private const string ExpectedVersion = "2.99";

    private static int _patched;
    private static int _skipped;
    private static readonly List<string> Report = new List<string>();

    private static void Line(string s) { Console.WriteLine(s); Report.Add(s); }
    private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Line("  [OK]   " + s); Console.ResetColor(); }
    private static void Skip(string s) { Console.ForegroundColor = ConsoleColor.DarkGray; Line("  [SKIP] " + s); Console.ResetColor(); _skipped++; }
    private static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Line("  [WARN] " + s); Console.ResetColor(); }

    internal static int Run(string[] rawArgs)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Line("PCL-Rebuilder 1.0.0  -  Minecraft download gate remover for Plain Craft Launcher");
        Line("not official, not affiliated with the PCL author; patches a COPY you own.");
        Line("");

        string targetPath = null;
        bool dryRun = false, restore = false, force = false;
        foreach (string a in rawArgs)
        {
            if (a.Equals("--restore", StringComparison.OrdinalIgnoreCase)) restore = true;
            else if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
            else if (a.Equals("--force", StringComparison.OrdinalIgnoreCase)) force = true;
            else if (a.StartsWith("--", StringComparison.Ordinal)) { Line("[FAIL] unknown option: " + a); return 2; }
            else targetPath = a;
        }
        if (targetPath == null) { PrintUsage(); return 2; }
        if (!File.Exists(targetPath)) { Line("[FAIL] file not found: " + targetPath); return 2; }
        targetPath = Path.GetFullPath(targetPath);

        string backupPath = Path.Combine(Path.GetDirectoryName(targetPath),
                                         Path.GetFileNameWithoutExtension(targetPath) + ".orig.exe");

        if (restore)
        {
            if (!File.Exists(backupPath)) { Line("[FAIL] no backup found: " + backupPath); return 1; }
            File.Copy(backupPath, targetPath, true);
            Line("[OK] restored from " + backupPath);
            return 0;
        }

        // ---------- 1) refuse to operate on ourselves ----------
        Line("target : " + targetPath);
        string selfPath = "";
        try { selfPath = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch { }
        if (selfPath.Length > 0 &&
            string.Equals(Path.GetFullPath(selfPath), targetPath, StringComparison.OrdinalIgnoreCase))
        {
            Line("[FAIL] that is PCL-Rebuilder itself, not the launcher.");
            Line("       pass the path to \"Plain Craft Launcher 2.exe\" (the official PCL binary).");
            return 2;
        }

        // ---------- 2) load + identity gate ----------
        ModuleDefMD module;
        try { module = ModuleDefMD.Load(targetPath); }
        catch (Exception ex) { Line("[FAIL] not a managed assembly: " + ex.Message); return 3; }

        if (FindType(module, "ModMain") == null || FindType(module, "ModSecret") == null)
        {
            Line("[FAIL] this file is not a Plain Craft Launcher build (no PCL.ModMain / PCL.ModSecret).");
            Line("       point it at the official \"Plain Craft Launcher 2.exe\", about 5,980,160 bytes.");
            return 3;
        }

        string fileVersion = "";
        try
        {
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(targetPath);
            string av = fvi.FileVersion ?? "";
            string pv = fvi.ProductVersion ?? "";
            fileVersion = ((av.Length > 0 ? av : pv) + " " + pv).Trim();
        }
        catch { }
        Line("version: " + (fileVersion.Length > 0 ? fileVersion : "<unknown>"));
        if (!force && fileVersion.IndexOf(ExpectedVersion, StringComparison.Ordinal) < 0)
        {
            Line("[FAIL] this build expects PCL " + ExpectedVersion + ".x (found \"" + fileVersion + "\").");
            Line("       pass --force only if you already checked the IL layout yourself.");
            module.Dispose();
            return 3;
        }

        if (module.Assembly != null && module.Assembly.HasPublicKey)
            Warn("the target is strong-name signed; patching invalidates the signature.");

        // ---------- 3) patches ----------
        Line("");
        Line("== patch sites ==");
        if (DispatchPatch.Apply(module, true)) { _patched++; Ok("P1 ModpackInstall: 7-way PackType dispatch restored"); }
        else Skip("P1 dispatch already present / not applicable");
        PatchPageDownloadInstallLoader(module);
        PatchCardMinecraftVisibility(module);
        if (ImportPackPatch.Apply(module, true))
        {
            _patched++;
            Ok("P4 ModModpack.ModpackInstall(): \u5BFC\u5165\u6574\u5408\u5305 entry restored (helper injected)");
        }
        else Skip("P4 import-pack entry already restored / not applicable");

        Line("");
        if (_patched == 0)
        {
            Line(_skipped > 0 ? "[DONE] nothing to do - already patched." : "[FAIL] no patch site matched this binary.");
            return _skipped > 0 ? 0 : 4;
        }
        if (dryRun)
        {
            Line("[DONE] dry run - " + _patched + " site(s) would be patched, file untouched.");
            return 0;
        }

        // ---------- 4) backup + write ----------
        if (!File.Exists(backupPath))
        {
            File.Copy(targetPath, backupPath, false);
            Line("backup : " + backupPath);
        }
        else Line("backup : " + backupPath + " (kept)");

        string tempOut = targetPath + ".rebuilding";
        try
        {
            module.Write(tempOut);
            module.Dispose();
            if (File.Exists(targetPath)) File.SetAttributes(targetPath, System.IO.FileAttributes.Normal);
            // Atomic swap: File.Copy would truncate the destination first, so a failure
            // (or the launcher still running) could leave the user with a 0-byte exe.
            if (File.Exists(targetPath)) File.Replace(tempOut, targetPath, null);
            else File.Move(tempOut, targetPath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
            Line("[FAIL] write failed, original left untouched: " + ex.Message);
            return 5;
        }

        Line("");
        Line("[DONE] patched " + _patched + " site(s). Use --restore to roll back.");
        Line("verify : run the launcher, open the download page, the version list should load.");
        try
        {
            File.WriteAllText(Path.ChangeExtension(targetPath, ".rebuilder.log"),
                              string.Join(Environment.NewLine, Report), Encoding.UTF8);
        }
        catch { }
        return 0;
    }

    private static void PrintUsage()
    {
        Line("usage:");
        Line("  PCL-Rebuilder.exe \"<path to Plain Craft Launcher 2.exe>\"        patch (makes *.orig.exe backup)");
        Line("  PCL-Rebuilder.exe \"...\" --dry-run                            report only, touch nothing");
        Line("  PCL-Rebuilder.exe \"...\" --restore                            roll back from *.orig.exe");
        Line("  PCL-Rebuilder.exe \"...\" --force                              skip the version check");
    }

    // ---------------------------------------------------------------- helpers
    private static void MakeNop(Instruction ins) { ins.OpCode = OpCodes.Nop; ins.Operand = null; }

    // Name of the type that really declares this member, unwrapping generic instantiations
    // (MemberRef on LoaderTask`2<string,X> reports a TypeSpec, whose Name is empty).
    private static string DeclTypeName(IMethod m)
    {
        if (m == null) return "";
        ITypeDefOrRef dt = m.DeclaringType;
        if (dt is TypeSpec ts && ts.TypeSig is GenericInstSig gis) dt = gis.GenericType.TypeDefOrRef;
        return dt == null ? "" : dt.Name;
    }

    private static TypeDef FindType(ModuleDefMD m, string name)
    {
        foreach (TypeDef t in m.GetTypes())
            if (t.Name == name) return t;
        return null;
    }

    private static MethodDef FindMethod(TypeDef t, string name, int paramCount)
    {
        if (t == null) return null;
        foreach (MethodDef md in t.Methods)
            if (md.Name == name && md.Parameters.Count == paramCount) return md;
        return null;
    }

    private static MethodDef FindMethodAny(TypeDef t, string name)
    {
        if (t == null) return null;
        foreach (MethodDef md in t.Methods)
            if (md.Name == name && md.HasBody) return md;
        return null;
    }

    private static bool BodyHasMarker(MethodDef md)
    {
        if (md == null || !md.HasBody) return false;
        foreach (Instruction ins in md.Body.Instructions)
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains(RestrictMarker))
                return true;
        return false;
    }

    // ---------------------------------------------------------------- P1
    private static void PatchModpackInstall(ModuleDefMD module)
    {
        TypeDef t = FindType(module, "ModModpack");
        MethodDef md = FindMethod(t, "ModpackInstall", 3);
        if (md == null || !md.HasBody) { Skip("P1 ModModpack.ModpackInstall(string,string,string) not found"); return; }
        md.Body.KeepOldMaxStack = true;

        IList<Instruction> ins = md.Body.Instructions;
        for (int i = 0; i < ins.Count; i++)
        {
            if (!(ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s && s.Contains(RestrictMarker))) continue;
            // expect: ldstr <marker>; newobj System.Exception::.ctor(string); throw
            if (i + 2 >= ins.Count) { Warn("P1: unexpected IL shape at IL_" + i.ToString("X4")); return; }
            if (ins[i + 1].OpCode != OpCodes.Newobj || ins[i + 2].OpCode != OpCodes.Throw)
            { Warn("P1: unexpected IL shape at IL_" + i.ToString("X4") + " -> " + ins[i + 1].OpCode + " / " + ins[i + 2].OpCode); return; }

            MakeNop(ins[i]); MakeNop(ins[i + 1]); MakeNop(ins[i + 2]);
            md.Body.KeepOldMaxStack = true;   // we only removed pushes, the old max is still valid
            _patched++;
            Ok("P1 ModpackInstall: restriction throw neutralised (execution now falls through to the install switch)");
            return;
        }
        Skip("P1 already patched");
    }

    // ---------------------------------------------------------------- P2
    private static void PatchPageDownloadInstallLoader(ModuleDefMD module)
    {
        TypeDef t = FindType(module, "PageDownloadInstall");
        MethodDef md = FindMethodAny(t, "LoaderInit");
        if (md == null || !md.HasBody) { Skip("P2 PageDownloadInstall.LoaderInit not found"); return; }

        TypeDef modDownload = FindType(module, "ModDownload");
        FieldDef loaderField = null;
        if (modDownload != null)
            foreach (FieldDef f in modDownload.Fields)
                if (f.Name == "DlClientListLoader") { loaderField = f; break; }
        if (loaderField == null) { Warn("P2: ModDownload.DlClientListLoader not found"); return; }

        md.Body.KeepOldMaxStack = true;
        IList<Instruction> ins = md.Body.Instructions;

        // already patched?
        for (int i = 0; i < ins.Count; i++)
            if (ins[i].OpCode == OpCodes.Ldsfld && ins[i].Operand == loaderField)
            { Skip("P2 already patched"); return; }

        // The gate builds `New LoaderTask(Of String, DlClientListResult)("关闭下载", Sub() Throw ...)`.
        // Locate the literal first, then the constructor that consumes it.
        int start = -1;
        for (int i = 0; i < ins.Count; i++)
            if (ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s && s.Contains(TaskNameMarker))
            { start = i; break; }
        if (start < 0) { Skip("P2 gate literal not found (page layout changed?)"); return; }

        int ctor = -1;
        for (int j = start + 1; j < ins.Count && j < start + 40; j++)
        {
            if (ins[j].OpCode != OpCodes.Newobj) continue;
            // NOTE: matching on the raw signature is wrong - the delegate ctor
            // Action`1<LoaderTask`2<..>>::.ctor also mentions LoaderTask. Resolve the
            // real declaring type name instead.
            if (ins[j].Operand is IMethod nm && DeclTypeName(nm) == "LoaderTask`2") { ctor = j; break; }
        }
        if (ctor < 0) { Warn("P2: LoaderTask constructor after the gate literal was not found"); return; }

        // Replace the whole "build a throwing LoaderTask" span with a single field read.
        // Everything in between becomes NOP so existing branch targets stay valid.
        ins[start].OpCode = OpCodes.Ldsfld;
        ins[start].Operand = loaderField;
        for (int j = start + 1; j <= ctor; j++) MakeNop(ins[j]);
        md.Body.KeepOldMaxStack = true;       // ldstr -> ldsfld keeps the stack depth identical
        _patched++;
        Ok("P2 PageDownloadInstall.LoaderInit: version list loader wired to ModDownload.DlClientListLoader");
    }

    // ---------------------------------------------------------------- P3
    // Visibility enum: Visible = 0, Hidden = 1, Collapsed = 2.
    // The restricted build folded `If(RestrictMcDownload, Collapsed, Visible)` into the
    // constant Collapsed, so the "Minecraft" card on the modify page never shows.
    private static void PatchCardMinecraftVisibility(ModuleDefMD module)
    {
        int found = 0;
        string[] owners = { "PageDownloadInstall", "PageDownloadModify" };
        foreach (string owner in owners)
        {
            TypeDef t = FindType(module, owner);
            if (t == null) continue;

            foreach (MethodDef md in t.Methods)
            {
                if (!md.HasBody) continue;
                IList<Instruction> ins = md.Body.Instructions;
                for (int i = 0; i < ins.Count; i++)
                {
                    bool isCollapsed = ins[i].OpCode == OpCodes.Ldc_I4_2 ||
                                       (ins[i].OpCode == OpCodes.Ldc_I4 && ins[i].Operand is int v && v == 2);
                    if (!isCollapsed) continue;
                    if (i + 1 >= ins.Count) continue;
                    Instruction next = ins[i + 1];
                    if (next.OpCode != OpCodes.Callvirt && next.OpCode != OpCodes.Call) continue;
                    if (!(next.Operand is IMethod mi) || mi.Name != "set_Visibility") continue;

                    bool isCard = false;
                    for (int k = Math.Max(0, i - 5); k < i; k++)
                    {
                        if (ins[k].Operand is IField f && f.Name == "CardMinecraft") { isCard = true; break; }
                        if (ins[k].Operand is IMethod m && m.Name == "get_CardMinecraft") { isCard = true; break; }
                    }
                    if (!isCard) continue;

                    ins[i].OpCode = OpCodes.Ldc_I4_0;   // Visibility.Visible
                    ins[i].Operand = null;
                    md.Body.KeepOldMaxStack = true;
                    _patched++; found++;
                    Ok("P3 " + t.Name + "." + md.Name + ": CardMinecraft Collapsed -> Visible");
                }
            }
        }
        if (found == 0) Skip("P3 CardMinecraft visibility already visible / not found");
    }
}
