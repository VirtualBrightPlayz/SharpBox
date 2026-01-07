using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using Sandbox;

public sealed class SharpBox {
    private sealed class Access {
        public required string name;
        public required string type;
        public required int count;
    }

    public string ScriptDir { get; set; }
    public List<AssemblyNameReference> WhitelistedAssemblies { get; private set; } = [];
    public List<Regex> Whitelist { get; private set; } = [];
    public List<Regex> Blacklist { get; private set; } = [];
    private AssemblyNameReference? _currentAssembly;
    private ConcurrentDictionary<string, Access> _touched = [];

    public SharpBox() {
        ScriptDir = Path.Join(Directory.GetCurrentDirectory(), "scripts");
    }

    public void AllowDefaults() {
        AddRange(Rules.BaseAccess);
        AddRange(Rules.Types);
        AddRange(Rules.Numerics);
        AddRange(Rules.Reflection);
        AddRange(Rules.Exceptions);
        AddRange(Rules.Diagnostics);
        AddRange(Rules.Async);
        // AddRange(Rules.CompilerGenerated);
    }

    public void AddRange(IEnumerable<string> range) {
        foreach (var line in range) {
            AddRule(line);
        }
    }

    public void AddRule(string line) {
        var wildcard = line.Trim();

        bool blacklist = wildcard.StartsWith('!');
        if (blacklist) {
            wildcard = wildcard[1..];
        }

        wildcard = Regex.Escape(wildcard).Replace("\\*", ".*");
        wildcard = $"^{wildcard}$";

        var regex = new Regex(wildcard, RegexOptions.Compiled);

        if (blacklist)
            Blacklist.Add(regex);
        else
            Whitelist.Add(regex);
    }

    public bool IsInWhitelist(string test) {
        if (Blacklist.Any(x => x.IsMatch(test)))
            return false;
        return Whitelist.Any(x => x.IsMatch(test));
    }

    public bool IsPassingRules() {
        var errs = new List<string>();
        foreach (var touch in _touched) {
            if (IsInWhitelist(touch.Key)) continue;
            errs.Add(touch.Key);
        }
        foreach (var err in errs) {
            Console.WriteLine(err);
        }
        return errs.Count == 0;
    }

    private void Touch(string name, string type) {
        _touched.AddOrUpdate(name, add => new Access { name = name, type = type, count = 1 },
        (name, update) => {
            update.count++;
            return update;
        });
    }

    private void Touch(TypeDefinition typeDef) {
        Touch($"{typeDef.Module.Assembly.Name.Name}/{typeDef.FullName}", "type");
    }

    private void Touch(TypeReference typeRef) {
        if (typeRef == null) return;
        switch (typeRef.MetadataType) {
            case MetadataType.Void:
            case MetadataType.Single:
            case MetadataType.Int16:
            case MetadataType.Int32:
            case MetadataType.Int64:
            case MetadataType.Boolean:
            case MetadataType.String:
                return;
        }
        if (typeRef.IsGenericParameter) return;
        if (typeRef is IModifierType modType) {
            Touch(modType.ModifierType);
            Touch(modType.ElementType);
            return;
        }
        if (typeRef is GenericInstanceType git) {
            foreach (var arg in git.GenericArguments) {
                Touch(arg);
            }
        }
        if (typeRef.IsArray || typeRef.IsByReference) {
            Touch(typeRef.GetElementType());
            return;
        }
        var typeDef = typeRef.Resolve();
        if (typeDef == null) {
            throw new Exception($"Failed to resolve {typeRef}");
        }
        if (typeDef.Module.Assembly.Name.Name == _currentAssembly?.Name && typeDef.Module.Assembly.Name.Version == _currentAssembly?.Version) return;
        if (WhitelistedAssemblies.Any(x => x.Name == typeDef.Module.Assembly.Name.Name && x.Version == typeDef.Module.Assembly.Name.Version)) return;
        Touch(typeDef);
    }

    private void Touch(MethodReference methodRef) {
        var typeDef = methodRef.Resolve();
        if (typeDef == null) {
            throw new Exception($"Failed to resolve {methodRef}");
        }
        if (typeDef.DeclaringType.Module.Assembly.Name.Name == _currentAssembly?.Name && typeDef.DeclaringType.Module.Assembly.Name.Version == _currentAssembly?.Version) return;
        if (typeDef.DeclaringType.Module.Assembly.Name.Name == "System.Private.CoreLib" &&
            typeDef.FullName == "System.Void System.Runtime.CompilerServices.RuntimeHelpers::EnsureSufficientExecutionStack()") {
                return;
        }
        Touch(typeDef);
    }

    private void Touch(MethodDefinition methodDef) {
        var touchName = $"{methodDef.Module.Assembly.Name.Name}/{methodDef.DeclaringType.FullName}.{methodDef.Name}";
        if (methodDef.HasGenericParameters) {
            var genericParams = string.Join(',', methodDef.GenericParameters.Select(x => x.Name.ToString()));
            if (!string.IsNullOrWhiteSpace(genericParams)) genericParams = $"<{genericParams}>";
            touchName += genericParams;
        }

        if (methodDef.HasParameters) {
            var methodParams = string.Join(", ", methodDef.Parameters.Select(x => x.ParameterType.ToString()));
            touchName += $"({methodParams})";
        } else {
            touchName += "()";
        }
        Touch(touchName, "method");
        // if (Touch(touchName, "method")) {
            // return true;
        // }
        Touch(methodDef.ReturnType);
        foreach (var param in methodDef.Parameters) {
            Touch(param.ParameterType);
        }
    }

    private void TestAttributes(Collection<CustomAttribute> attributes) {
        if (attributes == null) return;
        foreach (var attr in attributes) {
            TestAttribute(attr);
        }
    }

    private void TestAttribute(CustomAttribute attr) {
        Touch(attr.AttributeType);
        foreach (var arg in attr.ConstructorArguments) {
            Touch(arg.Type);
        }
    }

    private void TestOpCode(OpCode opcode) {
        if (opcode == OpCodes.Cpobj) {
            Touch("System.Private.CoreLib/System.Runtime.Opcode.Cpobj", "class");
        }
    }

    private void TestField(FieldDefinition fieldDef) {
        if (fieldDef.ContainsGenericParameter) return;
        if (fieldDef.HasLayoutInfo) {
            Touch("System.Private.CoreLib/System.Runtime.InteropServices.FieldOffset", "attribute");
        }
        if (fieldDef.HasMarshalInfo) {
            Touch("System.Private.CoreLib/System.Runtime.InteropServices.MarshalAs", "attribute");
        }
        Touch(fieldDef.FieldType);
    }

    private void TestProperty(PropertyDefinition propDef) {
        if (propDef.PropertyType.IsGenericParameter) return;
        Touch(propDef.PropertyType);
    }

    private void TestMethod(MethodDefinition methodDef) {
        if (methodDef.IsNative) Touch("System.Private.CoreLib/System.Runtime.InteropServices.DllImportAttribute", "attribute");
        if (methodDef.IsPInvokeImpl) Touch("System.Private.CoreLib/System.Runtime.InteropServices.DllImportAttribute", "attribute");
        if (methodDef.IsUnmanagedExport) Touch("System.Private.CoreLib/System.Runtime.InteropServices.DllImportAttribute", "attribute");
        Touch(methodDef);

        // if (_currentAssembly?.Name != methodDef.Module.Assembly.Name.Name || _currentAssembly?.Version != methodDef.Module.Assembly.Name.Version) return;

        Touch(methodDef.MethodReturnType.ReturnType);
        if (methodDef.HasBody) {
            foreach (var variable in methodDef.Body.Variables) {
                if (variable.IsPinned) Touch("System.Private.CoreLib/System.Security.UnverifiableCodeAttribute", "attribute");
            }
            foreach (var instruction in methodDef.Body.Instructions) {
                TestInstruction(methodDef, instruction);
            }
        }

        foreach (var parameter in methodDef.Parameters) {
            foreach (var attr in parameter.CustomAttributes) {
                Touch(attr.AttributeType);
            }
        }
    }

    private void TestInstruction(MethodDefinition methodDef, Instruction instruction) {
        TestOpCode(instruction.OpCode);

        if (instruction.Operand is string) return;
        if (instruction.Operand is float) return;
        if (instruction.Operand is double) return;
        if (instruction.Operand is int) return;
        if (instruction.Operand is sbyte) return;

        if (instruction.Operand is Instruction[] instructions) {
            foreach (var i in instructions) {
                TestInstruction(methodDef, i);
            }
            return;
        }
        if (instruction.Operand is MethodReference methodRef) {
            if (methodRef.DeclaringType.IsArray) {
                Touch(methodRef.DeclaringType.Resolve());
            } else {
                Touch(methodRef);
            }
            Touch(methodRef.ReturnType);
            if (methodRef.DeclaringType is GenericInstanceType git) {
                foreach (var param in git.GenericArguments) {
                    Touch(param);
                }
            }
            if (instruction.Operand is GenericInstanceMethod gim) {
                foreach (var param in gim.GenericArguments) {
                    Touch(param);
                }
            }
            foreach (var param in methodRef.GenericParameters) {
                Touch(param);
            }
            foreach (var param in methodRef.Parameters) {
                Touch(param.ParameterType);
            }
            if (methodRef.Name == WellKnownMemberNames.DestructorName && (instruction.OpCode.Code == Code.Ldftn || instruction.OpCode.Code == Code.Ldvirtftn)) {
				// Dummy invalid method
				Touch("System.Private.CoreLib/System.InvalidFinalizeMethodReference", "method");
			}
            return;
        }

        if (instruction.Operand is VariableDefinition varDef) {
            Touch(varDef.VariableType);
            return;
        }
        if (instruction.Operand is FieldDefinition fieldDef) {
            Touch(fieldDef.FieldType);
            return;
        }
        if (instruction.Operand is TypeReference typeRef) {
            Touch(typeRef);
            return;
        }
        if (instruction.Operand is FieldReference fieldRef) {
            Touch(fieldRef.FieldType);
            return;
        }
        if (instruction.Operand is ParameterDefinition paramDef) {
            Touch(paramDef.ParameterType);
            return;
        }
        if (instruction.Operand is Instruction inst && inst != instruction) {
            TestInstruction(methodDef, inst);
            return;
        }
    }

    private void TestBaseType(TypeDefinition parent, TypeReference baseType) {
        var baseInfo = baseType.Resolve();
        var baseTypeName = $"{baseInfo.Module.Assembly.Name.Name}{baseInfo.FullName}";

        if (baseTypeName == "System.Private.CoreLibSystem.ValueType") return;
        if (baseTypeName == "System.Private.CoreLibSystem.Enum") return;
        if (baseTypeName == "System.Private.CoreLibSystem.Object") return;

        var ctors = baseInfo.GetConstructors();
        var parentCtors = parent.GetConstructors().ToArray();

        if (parentCtors.Length == 0) {
            TestType(baseInfo);
            foreach (var ctor in ctors) {
                Touch(ctor);
            }
            return;
        }
    }

    private void TestType(TypeDefinition typeDef) {
        if ((typeDef.Attributes & Mono.Cecil.TypeAttributes.ExplicitLayout) != 0) {
            Touch("System.Private.CoreLib/System.Runtime.InteropServices.StructLayout", "attribute");
        }
        TestAttributes(typeDef.CustomAttributes);
        if (typeDef.IsArray) {
            TestType(typeDef.GetElementType().Resolve());
        }
        foreach (var member in typeDef.Fields) {
            TestField(member);
            TestAttributes(member.CustomAttributes);
        }
        foreach (var member in typeDef.Properties) {
            TestProperty(member);
            TestAttributes(member.CustomAttributes);
        }
        foreach (var member in typeDef.Interfaces) {
            Touch(member.InterfaceType);
        }
        if (typeDef.BaseType is not null) {
            TestBaseType(typeDef, typeDef.BaseType);
        }
        foreach (var member in typeDef.Methods) {
            TestMethod(member);
            TestAttributes(member.CustomAttributes);
        }
    }

    public void TestModule(ModuleDefinition moduleDef) {
        if (moduleDef.HasExportedTypes) {
            Touch("System.Private.CoreLib/System.Runtime.CompilerServices.TypeForwardedToAttribute", "attribute");
        }
        TestAttributes(moduleDef.CustomAttributes);
        foreach (var typeDef in moduleDef.Types) {
            TestType(typeDef);
        }
    }

    public bool CheckAssembly(AssemblyDefinition assemblyDef) {
        _currentAssembly = assemblyDef.Name;
        _touched.Clear();
        TestAttributes(assemblyDef.CustomAttributes);
        foreach (var moduleDef in assemblyDef.Modules) {
            TestModule(moduleDef);
        }
        var list = new List<string>();
        list.AddRange(WhitelistedAssemblies.Select(x => $"{x.Name}/"));
        list.Add($"{_currentAssembly.Name}/");
        foreach (var key in _touched.Keys) {
            if (list.Any(key.StartsWith)) {
                _touched.Remove(key, out _);
            }
        }
        return IsPassingRules();
    }

    public Task<object> RunCSharpTextAsync(string text) {
        var context = AssemblyLoadContext.GetLoadContext(GetType().Assembly);
        if (context == null) { return (Task<object>)Task.CompletedTask; }
        var asms = new List<string>();
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        asms.EnsureCapacity(loadedAssemblies.Length);
        foreach (var loadedAsm in loadedAssemblies) {
            if (loadedAsm == null) { continue; }
            if (!string.IsNullOrEmpty(loadedAsm.Location)) {
                asms.Add(loadedAsm.Location);
                continue;
            }
            var asmPath = Path.GetFullPath(Path.Join(AppDomain.CurrentDomain.BaseDirectory, $"{loadedAsm.GetName().Name}.dll"));
            if (!File.Exists(asmPath)) { continue; }
            asms.Add(asmPath);
        }
        var dllName = Path.Combine(Path.GetTempPath(), $"SharpBox-Script-{Random.Shared.Next()}.dll");
        var opts = ScriptOptions.Default.WithAllowUnsafe(false);
        var script = Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.Create(text, opts);
        var result = script.GetCompilation()
            .AddReferences(asms.Select(x => MetadataReference.CreateFromFile(x)))
            .Emit(dllName);
        if (!result.Success) {
            throw new CompilationErrorException(string.Join('\n', result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.ToString())), result.Diagnostics);
        }
        var asmDef = AssemblyDefinition.ReadAssembly(dllName);
        if (!CheckAssembly(asmDef)) {
            throw new AccessViolationException(dllName);
        }
        var asm = context!.LoadFromAssemblyPath(dllName);
        var entry = asm.ExportedTypes.First();
        var inst = Activator.CreateInstance(entry, [ new object[2] ]);
        if (inst == null) {
            return (Task<object>)Task.CompletedTask;
        }
        var evalResult = inst.GetType().GetMethods()?.First().Invoke(inst, []);
        return (Task<object>)(evalResult ?? Task.CompletedTask);
    }

    public bool RunCSharpFile(string fileName, out object result) {
        var scriptDir = ScriptDir;
        Directory.CreateDirectory(scriptDir);
        var files = Directory.GetFiles(scriptDir);
        var execPath = files.FirstOrDefault(x => Path.GetFileName(x) == $"{fileName}.cs");
        if (File.Exists(execPath)) {
            var text = File.ReadAllText(execPath);
            result = RunCSharpTextAsync(text).Result;
            return true;
        } else {
            result = (Task<object>)Task.CompletedTask;
            return false;
        }
    }
}