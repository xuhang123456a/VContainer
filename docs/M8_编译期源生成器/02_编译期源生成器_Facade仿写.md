# M8 编译期源生成器 · Facade 仿写

> 说明：完整 Roslyn `IIncrementalGenerator` 难以脱离 `Microsoft.CodeAnalysis` 独立编译。本 Facade 用**反射 + 字符串模板**模拟 Emitter 的核心：从一个 `Type` 生成等价于 `XxxGeneratedInjector` 的 C# 源码文本，复刻"构造选择 + ResolveOrParameter 调用形态 + 不支持场景跳过 + Component 抛异常"四个不变量。这能让你脱离 Roslyn 也理解"生成了什么、为什么与反射版对称"。

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| Roslyn 增量管线 / SyntaxProvider | 用反射遍历 Type 代替符号分析 | **简化**（脱 Roslyn） |
| 程序集过滤 / 两条候选流去重 | 砍掉（直接对给定 Type 生成） | **砍掉** |
| `Emitter` 生成 IInjector 源码 | 保留：生成 CreateInstance + Inject 文本 | **保留**（核心） |
| 构造选择镜像反射版 | 保留 | **保留**（对称性） |
| `ResolveOrParameter(...)` 调用形态 | 保留逐字生成 | **保留**（数据流一致） |
| Component → throw NotSupported | 保留 | **保留** |
| 不支持场景（嵌套/抽象/泛型/私有）报诊断 | 退化为返回 null + 原因字符串 | **简化** |
| `CodeWriter` 缩进/块作用域 | 保留极简版 | **保留** |

## 最小可编译复刻

```csharp
using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MiniVC.SourceGen
{
    [AttributeUsage(AttributeTargets.All)] public sealed class InjectAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class KeyAttribute : Attribute
    { public readonly object Key; public KeyAttribute(object k) => Key = k; }

    // 极简 CodeWriter（缩进 + 块）
    public sealed class CodeWriter
    {
        readonly StringBuilder sb = new(); int indent;
        public void Line(string s = "") => sb.AppendLine(s.Length == 0 ? "" : new string(' ', indent * 4) + s);
        public IDisposable Block(string head) { Line(head); Line(new string(' ', indent * 4) + "{"); indent++; return new End(this); }
        sealed class End : IDisposable { readonly CodeWriter w; public End(CodeWriter w) => this.w = w; public void Dispose() { w.indent--; w.Line(new string(' ', w.indent * 4) + "}"); } }
        public override string ToString() => sb.ToString();
    }

    public static class InjectorEmitter
    {
        // 返回生成的源码；不支持场景返回 null + reason
        public static string TryEmit(Type t, out string reason)
        {
            reason = null;
            if (t.IsNested)    { reason = "nested not supported"; return null; }
            if (t.IsAbstract)  { reason = "abstract not allowed"; return null; }
            if (t.IsGenericType){ reason = "generics not supported"; return null; }

            var w = new CodeWriter();
            w.Line("using System.Collections.Generic;");
            w.Line($"// generated for {t.FullName}");
            using (w.Block($"public sealed class {t.Name}GeneratedInjector : IInjector"))
            {
                if (!EmitCreate(t, w, out reason)) return null;
                w.Line();
                EmitInject(t, w);
            }
            return w.ToString();
        }

        static bool EmitCreate(Type t, CodeWriter w, out string reason)
        {
            reason = null;
            // 构造选择：[Inject] 优先 → 参数最多
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var annotated = ctors.Where(c => c.IsDefined(typeof(InjectAttribute), false)).ToArray();
            if (annotated.Length > 1) { reason = "multiple [Inject] ctor"; return false; }
            var ctor = annotated.FirstOrDefault() ?? ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor == null) { reason = "ctor not found"; return false; }
            if (ctor.IsPrivate) { reason = "private ctor not supported"; return false; }

            using (w.Block("public object CreateInstance(IObjectResolver resolver, IReadOnlyList<IInjectParameter> parameters)"))
            {
                // Unity Component 不能 new（此处用类型名约定模拟）
                if (t.BaseType?.FullName == "UnityEngine.Component")
                { w.Line($"throw new System.NotSupportedException(\"{t.Name} cannot be new\");"); return true; }

                var ps = ctor.GetParameters();
                if (ps.Length == 0) w.Line($"var __instance = new {t.Name}();");
                else
                {
                    w.Line($"var __instance = new {t.Name}(");
                    for (var i = 0; i < ps.Length; i++)
                        w.Line("    " + Resolve(ps[i].ParameterType, ps[i].Name, KeyOf(ps[i])) + (i + 1 < ps.Length ? "," : ""));
                    w.Line(");");
                }
                w.Line("Inject(__instance, resolver, parameters);");
                w.Line("return __instance;");
            }
            return true;
        }

        static void EmitInject(Type t, CodeWriter w)
        {
            using (w.Block("public void Inject(object instance, IObjectResolver resolver, IReadOnlyList<IInjectParameter> parameters)"))
            {
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                              .Where(f => f.IsDefined(typeof(InjectAttribute), false)).ToArray();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                               .Where(m => m.IsDefined(typeof(InjectAttribute), false)).ToArray();
                if (fields.Length == 0 && methods.Length == 0) { w.Line("return;"); return; }

                w.Line($"var __x = ({t.Name})instance;");
                foreach (var f in fields)                            // 字段：强类型直接赋值（无反射）
                    w.Line($"__x.{f.Name} = ({f.FieldType.Name}){Resolve(f.FieldType, f.Name, KeyOf(f))};");
                foreach (var m in methods)                           // 方法：强类型调用
                {
                    var ps = m.GetParameters();
                    var args = string.Join(", ", ps.Select(p => Resolve(p.ParameterType, p.Name, KeyOf(p))));
                    w.Line($"__x.{m.Name}({args});");
                }
            }
        }

        // 关键：与反射版完全相同的解析门面调用形态
        static string Resolve(Type type, string name, string key)
            => $"({type.Name})resolver.ResolveOrParameter(typeof({type.Name}), \"{name}\", parameters, {key})";

        static string KeyOf(ICustomAttributeProvider p)
        {
            var k = p.GetCustomAttributes(typeof(KeyAttribute), false).FirstOrDefault() as KeyAttribute;
            return k?.Key switch { null => "null", string s => $"\"{s}\"", var v => v.ToString() };
        }
    }
}
```

## 使用示例

```csharp
class Logger { }
class Service
{
    [Inject] Logger log;
    public Service(Logger logger) { }
    [Inject] public void Setup(Logger l) { }
}

string code = InjectorEmitter.TryEmit(typeof(Service), out var reason);
Console.WriteLine(code ?? $"// skipped: {reason}");
// 生成的 ServiceGeneratedInjector.CreateInstance/Inject 体内全是
// resolver.ResolveOrParameter(typeof(Logger), "...", parameters, null) —— 与反射版数据流一致
```

生成结果（节选）：

```csharp
public sealed class ServiceGeneratedInjector : IInjector
{
    public object CreateInstance(IObjectResolver resolver, IReadOnlyList<IInjectParameter> parameters)
    {
        var __instance = new Service(
            (Logger)resolver.ResolveOrParameter(typeof(Logger), "logger", parameters, null)
        );
        Inject(__instance, resolver, parameters);
        return __instance;
    }
    public void Inject(object instance, IObjectResolver resolver, IReadOnlyList<IInjectParameter> parameters)
    {
        var __x = (Service)instance;
        __x.log = (Logger)resolver.ResolveOrParameter(typeof(Logger), "log", parameters, null);
        __x.Setup((Logger)resolver.ResolveOrParameter(typeof(Logger), "l", parameters, null));
    }
}
```

## 取舍自检

- ✅ **保留**：生成 `IInjector` 同形契约、构造选择镜像反射版、`ResolveOrParameter` 逐字调用形态（保证数据流一致）、Component → throw、不支持场景跳过。
- ❌ **砍掉**：Roslyn 增量管线/SyntaxProvider/程序集过滤/两流去重、真实诊断描述、继承链精确去重（反射版已示范）、属性注入（同字段处理）。
- ⚠️ **最容易搞错**：编译期生成必须与运行时反射版**逐条对称**。比如构造选择规则、`ResolveOrParameter` 的参数顺序与 key 取值，一旦不一致，就会出现"装/不装源生成器，注入结果不同"的诡异 bug。本质上生成器只是把 M2 的运行时启发式"提前到编译期用文本表达"，语义必须 1:1 对齐。
