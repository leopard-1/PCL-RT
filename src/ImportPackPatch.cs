// =============================================================================
//  P4: restore the "导入整合包" entry point.
//
//  Official build folds `If RestrictMcDownload Then Hint(...) : Return End If`
//  and the compiler then deletes everything after the Return, so the whole body
//  of ModModpack.ModpackInstall() is gone (19 bytes: hint + ret). There is no IL
//  left to NOP, so P4 (a) injects two small static helpers and (b) rewrites the
//  entry point to call them. Nothing else in the assembly is touched.
// =============================================================================
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

internal static class ImportPackPatch
{
    private const string DialogTitle = "\u9009\u62E9\u6574\u5408\u5305\u538B\u7F29\u6587\u4EF6"; // 选择整合包压缩文件
    private const string FailHint = "\u5BFC\u5165\u6574\u5408\u5305\u5931\u8D25\uFF0C\u8BE6\u89C1\u65E5\u5FD7"; // 导入整合包失败，详见日志
    private const string ThreadName = "\u5BFC\u5165\u6574\u5408\u5305"; // 导入整合包

    public static bool Apply(ModuleDefMD module, bool verbose)
    {
        TypeDef modModpack = null;
        foreach (TypeDef t in module.GetTypes()) if (t.Name == "ModModpack") { modModpack = t; break; }
        if (modModpack == null) { System.Console.WriteLine("   [dbg] ModModpack=null"); return false; }

        MethodDef entry = null, install = null;
        foreach (MethodDef md in modModpack.Methods)
        {
            if (md.Name != "ModpackInstall") continue;
            if (md.Parameters.Count == 0) entry = md;
            else if (md.Parameters.Count == 3) install = md;
        }
        if (entry == null) { System.Console.WriteLine("   [dbg] entry=null"); return false; }
        if (install == null) { System.Console.WriteLine("   [dbg] install=null"); return false; }

        // already injected?
        foreach (MethodDef md in modModpack.Methods)
            if (md.Name == "PCLRebuild_ImportPack") { System.Console.WriteLine("   [dbg] already injected"); return false; }

        IMethod selectFile = FindMethod(module, "SelectFile", 4);
        if (selectFile == null) { System.Console.WriteLine("   [dbg] SelectFile=null"); return false; }
        IMethod runInThread = FindMethod(module, "RunInThread", 1);
        IMethod hint = FindMethod(module, "Hint", 3);
        if (runInThread == null) { System.Console.WriteLine("   [dbg] RunInThread=null"); return false; }
        if (hint == null) { System.Console.WriteLine("   [dbg] Hint=null"); return false; }

        if (verbose) System.Console.WriteLine("  [INFO] injecting SelectFile entry point (this cannot be a NOP patch)");

        // ---- static field carrying the picked path between the two helpers ----
        var pending = new FieldDefUser("PCLRebuild_PendingPack",
            new FieldSig(module.CorLibTypes.String),
            FieldAttributes.Private | FieldAttributes.Static);
        modModpack.Fields.Add(pending);

        // ---- Task<bool> type refs we need to synthesise ----
        // The delegate ctor takes (object, native int) = ELEMENT_TYPE_I. Building that sig by
        // hand with a VALUETYPE ref to System.IntPtr produces a *different* signature and the
        // runtime reports MissingMethodException, so reuse the compiler-emitted MemberRef.
        IMethod actionCtor = FindActionCtor(module);
        if (actionCtor == null)
        {
            System.Console.WriteLine("   [dbg] System.Action::.ctor not found in metadata");
            return false;
        }

        // Reuse the MemberRefs the compiler already emitted for List<string>. Hand-built
        // refs on a synthesised TypeSpec do not resolve (the loader ends up looking for the
        // member on the *open* generic type -> MissingMethodException at JIT time).
        IMethod getCount = FindListStringMember(module, "get_Count", 0);
        IMethod getItem = FindListStringMember(module, "get_Item", 1);
        if (getCount == null || getItem == null)
        {
            System.Console.WriteLine("   [dbg] List<string> accessors not found in metadata");
            return false;
        }

        // ---- helper 2: runs on a worker thread, swallows errors into a toast ----
        MethodDef installPending = new MethodDefUser("PCLRebuild_ImportPack_Pending",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
        modModpack.Methods.Add(installPending);

        var body2 = new CilBody();
        body2.InitLocals = true;
        body2.MaxStack = 8;

        Instruction tryStart = Instruction.Create(OpCodes.Ldsfld, pending);
        body2.Instructions.Add(tryStart);
        body2.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body2.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body2.Instructions.Add(Instruction.Create(OpCodes.Call, install));
        body2.Instructions.Add(Instruction.Create(OpCodes.Pop));

        Instruction tail = Instruction.Create(OpCodes.Ret);
        body2.Instructions.Add(Instruction.Create(OpCodes.Leave_S, tail));

        Instruction handlerStart = Instruction.Create(OpCodes.Pop);   // discard the exception
        body2.Instructions.Add(handlerStart);
        body2.Instructions.Add(Instruction.Create(OpCodes.Ldstr, FailHint));
        body2.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_2)); // HintType.Red
        body2.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        body2.Instructions.Add(Instruction.Create(OpCodes.Call, hint));
        // A handler may not fall through into the instruction after it - it must leave.
        body2.Instructions.Add(Instruction.Create(OpCodes.Leave_S, tail));

        body2.Instructions.Add(tail);

        body2.ExceptionHandlers.Add(new ExceptionHandler
        {
            HandlerType = ExceptionHandlerType.Catch,
            CatchType = new TypeRefUser(module, "System", "Exception", module.CorLibTypes.AssemblyRef),
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = tail
        });
        installPending.Body = body2;

        // ---- helper 1: the entry point ----
        MethodDef importPack = new MethodDefUser("PCLRebuild_ImportPack",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
        modModpack.Methods.Add(importPack);

        var body = new CilBody();
        body.InitLocals = true;
        body.MaxStack = 8;
        var vFilter = new Local(selectFile.MethodSig.Params[3]);       // Nullable<OneOf<..>>
        var vList = new Local(selectFile.MethodSig.RetType);           // List<string>
        var vFile = new Local(module.CorLibTypes.String);
        body.Variables.Add(vFilter);
        body.Variables.Add(vList);
        body.Variables.Add(vFile);

        Instruction ret = Instruction.Create(OpCodes.Ret);
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, DialogTitle));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));   // MultiSelect = False
        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));     // String arg
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFilter)); // default(Nullable<...>) => no filter
        body.Instructions.Add(Instruction.Create(OpCodes.Call, selectFile));
        body.Instructions.Add(Instruction.Create(OpCodes.Stloc, vList));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vList));
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, ret));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vList));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getCount));
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, ret));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vList));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getItem));
        body.Instructions.Add(Instruction.Create(OpCodes.Stloc, vFile));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, pending));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, installPending));
        body.Instructions.Add(Instruction.Create(OpCodes.Newobj, actionCtor));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, runInThread));
        body.Instructions.Add(ret);
        importPack.Body = body;

        // ---- rewrite the entry point so it delegates to our helper ----
        entry.Body.Instructions.Clear();
        entry.Body.Variables.Clear();
        entry.Body.ExceptionHandlers.Clear();
        entry.Body.Instructions.Add(Instruction.Create(OpCodes.Call, importPack));
        Instruction entryRet = Instruction.Create(OpCodes.Ret);
        entry.Body.Instructions.Add(entryRet);
        entry.Body.MaxStack = 1;
        entry.Body.InitLocals = false;

        return true;
    }

    // Finds an existing MemberRef for the System.Action delegate constructor.
    private static IMethod FindActionCtor(ModuleDefMD module)
    {
        foreach (MemberRef mr in module.GetMemberRefs())
        {
            if (mr.Name != ".ctor" || mr.MethodSig == null) continue;
            if (mr.MethodSig.Params.Count != 2) continue;
            var dt = mr.DeclaringType;
            if (dt == null || dt.FullName != "System.Action") continue;
            return mr;
        }
        return null;
    }
    // Finds an existing MemberRef for a method on List`1<String> (parent is a TypeSpec).
    private static IMethod FindListStringMember(ModuleDefMD module, string name, int paramCount)
    {
        foreach (MemberRef mr in module.GetMemberRefs())
        {
            if (mr.Name != name || mr.MethodSig == null) continue;
            if (mr.MethodSig.Params.Count != paramCount) continue;
            var ts = mr.DeclaringType as TypeSpec;
            if (ts == null) continue;
            var gis = ts.TypeSig as GenericInstSig;
            if (gis == null || gis.GenericArguments.Count != 1) continue;
            if (gis.GenericArguments[0].FullName != "System.String") continue;
            var td = gis.GenericType.TypeDefOrRef;
            if (td == null || td.Name != "List`1") continue;
            return mr;
        }
        return null;
    }
    private static IMethod FindMethod(ModuleDefMD module, string name, int paramCount)
    {
        // Table-based lookup: MemberRefs carry the exact signature, so no ambiguity
        // between overloads and no need to walk every method body.
        foreach (MemberRef mr in module.GetMemberRefs())
        {
            if (mr.Name != name || mr.MethodSig == null) continue;
            if (mr.MethodSig.Params.Count == paramCount) return mr;
        }
        foreach (TypeDef t in module.GetTypes())
        {
            if (!t.HasMethods) continue;
            foreach (MethodDef md in t.Methods)
                if (md.Name == name && md.Parameters.Count == paramCount) return md;
        }
        return null;
    }
}
