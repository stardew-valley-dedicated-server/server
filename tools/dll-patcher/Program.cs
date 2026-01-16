// NOTE: This is a very early version created to check if this approach works/is feasible.
// While it is functional, it definitely needs to be properly refactored and cleaned up.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

// Constants
var addLogStatement = true;

// Functions
static void Log(string message, string context = "Patcher")
{
    var timestamp = DateTime.Now.ToString("HH:mm:ss");
    Console.WriteLine($"[{timestamp} INFO  {context}] {message}");
}

// Main
if (args.Length < 1)
{
    Log("Usage: SDVPatcher <path-to-Stardew Valley.dll>");
    return 1;
}

var dllPath = args[0];

if (!File.Exists(dllPath))
{
    Log($"Error: File not found: {dllPath}");
    return 1;
}

Log($"Patching {dllPath}...");

try
{
    var originalPath = dllPath + ".original";
    var patchInfoPath = dllPath + ".patch-info";

    // Get the patcher executable path
    var patcherExePath = Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);

    if (string.IsNullOrEmpty(patcherExePath))
    {
        // For single-file publish, use process path
        patcherExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
    }

    Log($"Hashing patcher at: {patcherExePath}");

    if (string.IsNullOrEmpty(patcherExePath) || !File.Exists(patcherExePath))
    {
        Log("ERROR: Could not determine patcher executable path for hashing");
        return 1;
    }

    // Compute hash of current patcher
    string currentPatcherHash;
    using (var sha256 = SHA256.Create())
    using (var stream = File.OpenRead(patcherExePath))
    {
        var hashBytes = sha256.ComputeHash(stream);
        currentPatcherHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    Log($"Patcher hash: {currentPatcherHash.Substring(0, 16)}...");

    // Check if already patched with this version of patcher
    if (File.Exists(patchInfoPath))
    {
        var storedHash = File.ReadAllText(patchInfoPath).Trim();
        if (storedHash == currentPatcherHash)
        {
            Log("DLL already patched with current patcher version - skipping");
            return 0;
        }
        else
        {
            Log("Patcher version changed - restoring original DLL and re-patching");
            if (File.Exists(originalPath))
            {
                File.Copy(originalPath, dllPath, true);
                Log("Original DLL restored");
            }
            else
            {
                Log("Warning: No original DLL found to restore, patching current version");
            }
        }
    }

    // Preserve unpatched original DLL (first time only)
    if (!File.Exists(originalPath))
    {
        Log($"Preserving unpatched original: {originalPath}");
        File.Copy(dllPath, originalPath, false);
    }
    else
    {
        Log($"Unpatched original already preserved: {originalPath}");
    }

    // Set up assembly resolver to find referenced DLLs in the game directory
    var gameDir = Path.GetDirectoryName(dllPath);
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(gameDir);

    var readerParams = new ReaderParameters
    {
        ReadWrite = true,
        AssemblyResolver = resolver
    };

    var module = ModuleDefinition.ReadModule(dllPath, readerParams);

    var game1 = module.Types.FirstOrDefault(t => t.FullName == "StardewValley.Game1");
    if (game1 == null)
    {
        Log("Error: Could not find StardewValley.Game1 type");
        return 1;
    }

    var initMethod = game1.Methods.FirstOrDefault(m => m.Name == "Initialize");
    if (initMethod == null)
    {
        Log("Error: Could not find Initialize method");
        return 1;
    }

    var processor = initMethod.Body.GetILProcessor();
    var instructions = initMethod.Body.Instructions.ToList();

    bool patched = false;

    Log($"Scanning {instructions.Count} IL instructions in Initialize method...");

    // Find all calls to DoThreadedInitTask for debugging
    for (int i = 0; i < instructions.Count; i++)
    {
        if (instructions[i].OpCode == OpCodes.Call || instructions[i].OpCode == OpCodes.Callvirt)
        {
            var method = instructions[i].Operand as MethodReference;
            if (method?.Name == "DoThreadedInitTask")
            {
                Log($"  Found DoThreadedInitTask at offset {instructions[i].Offset} (index {i}):");

                // Print surrounding instructions
                for (int j = Math.Max(0, i - 3); j <= Math.Min(instructions.Count - 1, i + 2); j++)
                {
                    string marker = (j == i) ? " >>> " : "     ";
                    var inst = instructions[j];
                    string operandStr = inst.Operand != null ? $" {inst.Operand}" : "";

                    if (inst.Operand is MethodReference mr)
                    {
                        operandStr = $" {mr.DeclaringType.Name}::{mr.Name}";
                    }

                    Log($"{marker}[{j}] {inst.OpCode}{operandStr}");
                }
            }
        }
    }

    // Find and remove the `DoThreadedInitTask(InitializeSounds)` call
    for (int i = 0; i < instructions.Count - 4; i++)
    {
        // Check for the 5-instruction pattern
        if (i > 0 &&
            instructions[i - 1].OpCode == OpCodes.Ldarg_0 &&    // 'this' for method call
            instructions[i].OpCode == OpCodes.Ldarg_0 &&        // 'this' for delegate
            instructions[i + 1].OpCode == OpCodes.Ldftn &&      // ldftn
            instructions[i + 2].OpCode == OpCodes.Newobj &&     // ThreadStart
            instructions[i + 3].OpCode == OpCodes.Call)         // DoThreadedInitTask
        {
            var callMethod = instructions[i + 3].Operand as MethodReference;
            var ftnMethod = instructions[i + 1].Operand as MethodReference;
            var newobjMethod = instructions[i + 2].Operand as MethodReference;

            if (ftnMethod?.Name == "InitializeSounds" &&
                callMethod?.Name == "DoThreadedInitTask" &&
                newobjMethod?.DeclaringType?.Name == "ThreadStart")
            {
                Log($"Patching InitializeSounds call at IL offset {instructions[i - 1].Offset}:");

                var toRemove = new[] { instructions[i - 1], instructions[i], instructions[i + 1], instructions[i + 2], instructions[i + 3] };

                // Check if any instruction branches to one of these
                Log("  Checking for branch targets...");
                bool hasBranchTarget = false;
                foreach (var inst in instructions)
                {
                    if (inst.Operand is Instruction target && toRemove.Contains(target))
                    {
                        Log($"    WARNING: Instruction at {inst.Offset} branches to instruction we're removing!");
                        hasBranchTarget = true;
                    }
                }

                // Check exception handlers
                if (initMethod.Body.HasExceptionHandlers)
                {
                    Log("  Checking exception handlers...");
                    foreach (var handler in initMethod.Body.ExceptionHandlers)
                    {
                        if (toRemove.Contains(handler.TryStart) || toRemove.Contains(handler.TryEnd) ||
                            toRemove.Contains(handler.HandlerStart) || toRemove.Contains(handler.HandlerEnd))
                        {
                            Log($"    WARNING: Exception handler references instruction we're removing!");
                            hasBranchTarget = true;
                        }
                    }
                }

                if (!hasBranchTarget)
                {
                    Log("  No branch targets found - safe to remove");
                }

                Log("  Removing instructions...");

                // Store the instruction before the removed block for insertion point
                var insertAfterInstruction = instructions[i - 2];

                foreach (var inst in toRemove)
                {
                    Log($"    Removing: {inst.OpCode} at offset {inst.Offset}");
                    processor.Remove(inst);
                }

                if (addLogStatement)
                {
                    Log("  Adding runtime log statement...");

                    var writeLineMethod = module.ImportReference(
                        typeof(Console).GetMethod("WriteLine", [typeof(string)])
                    );

                    // Create log message with timestamp prefix
                    var ansiBlack = "\u001b[30m";
                    var ansiReset = "\u001b[0m";
                    var logLevel = "TRACE";
                    var logName = "patch";
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");

                    var logFormat = "DLL patched successfully";
                    var logMessage = $"{ansiBlack}[{timestamp} {logLevel} {logName}] {logFormat}{ansiReset}";

                    // Create instructions for insertion
                    var ldstr = processor.Create(OpCodes.Ldstr, logMessage);
                    var callWriteLine = processor.Create(OpCodes.Call, writeLineMethod);

                    // Insert after the instruction that comes before the removed block
                    processor.InsertAfter(insertAfterInstruction, ldstr);
                    processor.InsertAfter(ldstr, callWriteLine);

                    Log($"  Inserted runtime log");
                }

                Log($"  After patching, Initialize method has {initMethod.Body.Instructions.Count} instructions");

                // Dump instructions around the patched area for verification
                Log("  IL after patch (instructions 35-45):");
                var afterInstructions = initMethod.Body.Instructions;
                for (int j = Math.Max(0, 35); j < Math.Min(afterInstructions.Count, 45); j++)
                {
                    var inst = afterInstructions[j];
                    string operandStr = "";
                    if (inst.Operand is MethodReference mr)
                        operandStr = $" {mr.DeclaringType.Name}::{mr.Name}";
                    else if (inst.Operand != null)
                        operandStr = $" {inst.Operand}";

                    Log($"    [{j}] {inst.OpCode}{operandStr}");
                }

                patched = true;
                break;
            }
        }
    }

    if (patched)
    {
        Log("  Writing patched module...");
        module.Write();
        module.Dispose();

        Log($"Successfully patched {dllPath}");
        Log("InitializeSounds call removed from Game1.Initialize()");

        // Store patcher hash to indicate successful patch
        File.WriteAllText(patchInfoPath, currentPatcherHash);
        Log($"Patch info saved: {patchInfoPath}");

        return 0;
    }
    else
    {
        module.Dispose();
        Log("DLL is already patched or InitializeSounds call not found.");
        Log("Skipping patch - no changes made");
        return 0; // Not a fatal error - DLL is already in desired state
    }
}
catch (Exception ex)
{
    Log($"Error patching DLL: {ex.Message}");
    Log(ex.StackTrace ?? "");
    return 1;
}
