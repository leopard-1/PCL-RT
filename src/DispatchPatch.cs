// =============================================================================
//  P1 (real form): restore the modpack install dispatch.
//
//  The restricted build ends ModpackInstall(file,name,logo) with a Throw, which
//  makes the whole `Select Case PackType` unreachable -> the compiler deleted the
//  dispatch. The 7 InstallPack* implementations are still present in the assembly,
//  so we re-emit the switch exactly as the open-source source code has it.
//
//  Locals of the original method (verified against the official IL dump):
//      V_0 LoaderCombo   (return slot)
//      V_1 ZipArchive    (Archive)
//      V_2 String        (ArchiveBaseFolder)
//      V_3 String        (TargetFolder)
//      V_4 Int32         (PackType)
// =============================================================================
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

internal static class DispatchPatch
{
    private const string RestrictMarker = "\u4E3A\u9075\u5B88\u76F8\u5173\u6CD5\u5F8B\u6CD5\u89C4";

    public static bool Apply(ModuleDefMD module, bool verbose)
    {
        TypeDef t = null;
        foreach (TypeDef x in module.GetTypes()) if (x.Name == "ModModpack") { t = x; break; }
        if (t == null) return false;

        MethodDef md = null;
        foreach (MethodDef m in t.Methods) if (m.Name == "ModpackInstall" && m.Parameters.Count == 3) { md = m; break; }
        if (md == null || !md.HasBody) return false;

        // already dispatched?
        foreach (Instruction ins in md.Body.Instructions)
            if (ins.OpCode == OpCodes.Switch) return false;

        var bodies = md.Body;
        var ins2 = bodies.Instructions;
        if (bodies.Variables.Count < 5) return false;
        Local vRet = bodies.Variables[0];
        Local vArchive = bodies.Variables[1];
        Local vBase = bodies.Variables[2];
        Local vPack = bodies.Variables[4];

        MethodDef curseForge = null, hmcl = null, mmc = null, mcbbs = null, modrinth = null, launcherPack = null, compress = null;
        foreach (MethodDef m in t.Methods)
        {
            switch (m.Name.String)
            {
                case "InstallPackCurseForge": curseForge = m; break;
                case "InstallPackHMCL": hmcl = m; break;
                case "InstallPackMMC": mmc = m; break;
                case "InstallPackMCBBS": mcbbs = m; break;
                case "InstallPackModrinth": modrinth = m; break;
                case "InstallPackLauncherPack": launcherPack = m; break;
                case "InstallPackCompress": compress = m; break;
            }
        }
        if (curseForge == null || hmcl == null || mmc == null || mcbbs == null ||
            modrinth == null || launcherPack == null || compress == null)
        {
            if (verbose) System.Console.WriteLine("   [dbg] not all InstallPack* implementations found");
            return false;
        }

        // locate the restriction throw (3 instructions) and drop it
        int at = -1;
        for (int i = 0; i < ins2.Count; i++)
            if (ins2[i].OpCode == OpCodes.Ldstr && ins2[i].Operand is string s && s.Contains(RestrictMarker)) { at = i; break; }
        if (at < 0 || at + 2 >= ins2.Count) return false;
        // Do NOT remove them: they may be exception-handler boundaries. Reuse the three
        // Instruction objects for the head of the dispatch instead.

        // epilogue, appended after endfinally: load the return slot and ret
        Instruction epilogue = Instruction.Create(OpCodes.Ldloc, vRet);
        Instruction epilogueRet = Instruction.Create(OpCodes.Ret);
        ins2.Add(epilogue);
        ins2.Add(epilogueRet);

        Instruction L0 = Instruction.Create(OpCodes.Ldarg_0);
        Instruction L1 = Instruction.Create(OpCodes.Ldarg_0);
        Instruction L2 = Instruction.Create(OpCodes.Ldarg_0);
        Instruction L3 = Instruction.Create(OpCodes.Ldarg_0);
        Instruction L4 = Instruction.Create(OpCodes.Ldarg_0);
        Instruction L9 = Instruction.Create(OpCodes.Ldarg_0);
        Instruction Ld = Instruction.Create(OpCodes.Ldarg_0);

        var sw = Instruction.Create(OpCodes.Switch, new Instruction[] { L0, L1, L2, L3, L4, Ld, Ld, Ld, Ld, L9 });

        var block = new List<Instruction>();
        block.Add(Instruction.Create(OpCodes.Ldloc, vPack));
        block.Add(sw);
        block.Add(Instruction.Create(OpCodes.Br, Ld));

        // case 0: CurseForge(File, Archive, Base, InstanceName, Logo)
        block.Add(L0);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Ldloc, vBase));
        block.Add(Instruction.Create(OpCodes.Ldarg_1));
        block.Add(Instruction.Create(OpCodes.Ldarg_2));
        block.Add(Instruction.Create(OpCodes.Call, curseForge));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        // case 1: HMCL(File, Archive, Base)
        block.Add(L1);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Ldloc, vBase));
        block.Add(Instruction.Create(OpCodes.Call, hmcl));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        // case 2: MMC(File, Archive, Base)
        block.Add(L2);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Ldloc, vBase));
        block.Add(Instruction.Create(OpCodes.Call, mmc));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        // case 3: MCBBS(File, Archive, Base, InstanceName)
        block.Add(L3);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Ldloc, vBase));
        block.Add(Instruction.Create(OpCodes.Ldarg_1));
        block.Add(Instruction.Create(OpCodes.Call, mcbbs));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        // case 4: Modrinth(File, Archive, Base, InstanceName, Logo)
        block.Add(L4);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Ldloc, vBase));
        block.Add(Instruction.Create(OpCodes.Ldarg_1));
        block.Add(Instruction.Create(OpCodes.Ldarg_2));
        block.Add(Instruction.Create(OpCodes.Call, modrinth));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        // case 9: LauncherPack(File, Archive, Base)
        block.Add(L9);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Ldloc, vBase));
        block.Add(Instruction.Create(OpCodes.Call, launcherPack));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        // default: Compress(File, Archive)
        block.Add(Ld);
        block.Add(Instruction.Create(OpCodes.Ldloc, vArchive));
        block.Add(Instruction.Create(OpCodes.Call, compress));
        block.Add(Instruction.Create(OpCodes.Leave, epilogue));

        ins2[at].OpCode = OpCodes.Ldloc; ins2[at].Operand = vPack;
        ins2[at + 1].OpCode = OpCodes.Switch; ins2[at + 1].Operand = sw.Operand;
        ins2[at + 2].OpCode = OpCodes.Br; ins2[at + 2].Operand = Ld;
        for (int i = 3; i < block.Count; i++) ins2.Insert(at + i, block[i]);

        // The finally handler used to end at the end of the method; give it an explicit end
        // so the appended epilogue is outside every protected region.
        foreach (ExceptionHandler eh in bodies.ExceptionHandlers)
            if (eh.HandlerEnd == null) eh.HandlerEnd = epilogue;

        bodies.MaxStack = 8;
        bodies.KeepOldMaxStack = true;
        if (verbose) System.Console.WriteLine("   [INFO] re-emitted the 7-way PackType dispatch");
        return true;
    }
}
