# M2 类型分析与反射注入器 · Facade 仿写

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| `IInjector`（Inject + CreateInstance） | 保留同名双方法契约 | **保留** |
| 构造选择：[Inject] 优先 → 参数最多 | 保留 | **保留**（核心不变量） |
| 继承链向上扫描 字段/属性/方法 | 保留字段+方法；属性可省 | **简化** |
| 同名/override 去重 | 保留字段名去重 + 方法 GetBaseDefinition 去重 | **保留** |
| `[Key]` 预抽取 | 保留为 `object[] keys` | **保留** |
| `InjectorCache` 三态选择 | 退化为「缓存 + 仅反射」 | **简化**（砍源生成/IL 分支） |
| 参数数组用 `CappedArrayPool` 租借 | 直接 `new object[]` | **简化**（保留正确性，牺牲零 GC） |
| `ResolveOrParameter` 覆盖参数 | 保留 typed + named 匹配 | **保留** |
| 循环依赖 DFS 检测 | 保留最小版 | **保留**（最有价值） |

## 最小可编译复刻

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MiniVC.Injection
{
    public interface IObjectResolver { object Resolve(Type type, object key = null); }

    public interface IInjectParameter
    {
        bool Match(Type type, string name);
        object GetValue(IObjectResolver resolver);
    }
    public sealed class TypedParameter : IInjectParameter
    {
        readonly Type t; readonly object v;
        public TypedParameter(Type t, object v) { this.t = t; this.v = v; }
        public bool Match(Type type, string _) => type == t;
        public object GetValue(IObjectResolver _) => v;
    }
    public sealed class NamedParameter : IInjectParameter
    {
        readonly string n; readonly object v;
        public NamedParameter(string n, object v) { this.n = n; this.v = v; }
        public bool Match(Type _, string name) => name == n;
        public object GetValue(IObjectResolver _) => v;
    }

    public interface IInjector
    {
        void Inject(object instance, IObjectResolver r, IReadOnlyList<IInjectParameter> p);
        object CreateInstance(IObjectResolver r, IReadOnlyList<IInjectParameter> p);
    }

    // ---- 注入计划快照 ----
    public sealed class InjectTypeInfo
    {
        public readonly Type Type;
        public readonly ConstructorInfo Ctor;
        public readonly ParameterInfo[] CtorParams;
        public readonly object[] CtorKeys;
        public readonly List<(MethodInfo m, ParameterInfo[] p, object[] keys)> Methods;
        public readonly List<FieldInfo> Fields;

        public InjectTypeInfo(Type t, ConstructorInfo c, List<(MethodInfo, ParameterInfo[], object[])> m, List<FieldInfo> f)
        { Type = t; Ctor = c; CtorParams = c?.GetParameters() ?? Array.Empty<ParameterInfo>();
          CtorKeys = Keys(CtorParams); Methods = m; Fields = f; }

        public static object[] Keys(ParameterInfo[] ps) =>
            ps.Select(p => (object)p.GetCustomAttribute<KeyAttribute>()?.Key).ToArray();
    }

    [AttributeUsage(AttributeTargets.All)] public sealed class InjectAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class KeyAttribute : Attribute
    { public readonly object Key; public KeyAttribute(object key) { Key = key; } }

    public static class TypeAnalyzer
    {
        static readonly ConcurrentDictionary<Type, InjectTypeInfo> Cache = new();
        public static InjectTypeInfo Analyze(Type type) => Cache.GetOrAdd(type, Build);

        static InjectTypeInfo Build(Type type)
        {
            // 构造选择：[Inject] 优先，否则参数最多
            ConstructorInfo ctor = null; int max = -1; int annotated = 0;
            foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (c.IsDefined(typeof(InjectAttribute), false))
                {
                    if (++annotated > 1) throw new InvalidOperationException($"multiple [Inject] ctor on {type}");
                    ctor = c;
                }
                else if (annotated == 0 && c.GetParameters().Length > max)
                { ctor = c; max = c.GetParameters().Length; }
            }

            var fields = new List<FieldInfo>();
            var methods = new List<(MethodInfo, ParameterInfo[], object[])>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(flags))
                    if (m.IsDefined(typeof(InjectAttribute), false) &&
                        !methods.Any(x => x.Item1.GetBaseDefinition() == m.GetBaseDefinition()))
                    { var ps = m.GetParameters(); methods.Add((m, ps, InjectTypeInfo.Keys(ps))); }

                foreach (var f in t.GetFields(flags))
                    if (f.IsDefined(typeof(InjectAttribute), false) && fields.All(x => x.Name != f.Name))
                        fields.Add(f);
            }
            return new InjectTypeInfo(type, ctor, methods, fields);
        }
    }

    public sealed class ReflectionInjector : IInjector
    {
        readonly InjectTypeInfo info;
        ReflectionInjector(InjectTypeInfo i) => info = i;
        public static ReflectionInjector Build(Type t) => new ReflectionInjector(TypeAnalyzer.Analyze(t));

        public object CreateInstance(IObjectResolver r, IReadOnlyList<IInjectParameter> p)
        {
            var args = new object[info.CtorParams.Length];
            for (var i = 0; i < args.Length; i++)
                args[i] = ResolveOrParameter(r, info.CtorParams[i].ParameterType, info.CtorParams[i].Name, p, info.CtorKeys[i]);
            var instance = info.Ctor.Invoke(args);
            Inject(instance, r, p);
            return instance;
        }

        public void Inject(object instance, IObjectResolver r, IReadOnlyList<IInjectParameter> p)
        {
            foreach (var f in info.Fields)                          // 1) 字段
                f.SetValue(instance, ResolveOrParameter(r, f.FieldType, f.Name, p,
                    f.GetCustomAttribute<KeyAttribute>()?.Key));
            foreach (var (m, ps, keys) in info.Methods)             // 2) 方法（属性此处略）
            {
                var args = new object[ps.Length];
                for (var i = 0; i < args.Length; i++)
                    args[i] = ResolveOrParameter(r, ps[i].ParameterType, ps[i].Name, p, keys[i]);
                m.Invoke(instance, args);
            }
        }

        static object ResolveOrParameter(IObjectResolver r, Type type, string name,
            IReadOnlyList<IInjectParameter> parameters, object key)
        {
            if (parameters == null) return r.Resolve(type, key);
            for (var i = 0; i < parameters.Count; i++)              // 覆盖参数先到先得
                if (parameters[i].Match(type, name)) return parameters[i].GetValue(r);
            return r.Resolve(type, key);
        }
    }

    public static class InjectorCache
    {
        static readonly ConcurrentDictionary<Type, IInjector> Injectors = new();
        public static IInjector GetOrBuild(Type type) =>
            Injectors.GetOrAdd(type, t =>
            {
                // 三态选择的简化：只保留「源生成探测 + 反射兜底」
                var gen = t.Assembly.GetType($"{t.FullName}GeneratedInjector", false);
                return gen != null ? (IInjector)Activator.CreateInstance(gen) : ReflectionInjector.Build(t);
            });
    }
}
```

## 使用示例

```csharp
class Logger { }
class Service
{
    readonly Logger log;
    public Service(Logger log) => this.log = log;     // 构造注入（参数最多）
    [Inject] int retries;                              // 字段注入
    [Inject] public void Setup(Logger l) { }           // 方法注入
}

IInjector injector = InjectorCache.GetOrBuild(typeof(Service));
var s = (Service)injector.CreateInstance(myResolver, null);
```

## 取舍自检

- ✅ **保留**：构造选择启发式、继承链扫描 + 同名/override 去重、`[Key]` 预抽取、`ResolveOrParameter` 覆盖匹配、`InjectorCache` 的「源生成优先、反射兜底」。
- ❌ **砍掉**：`CappedArrayPool` 参数数组租借（用 `new` 代替，牺牲零 GC）、属性注入、IL 织入分支、循环依赖检测（生产必备，此处为篇幅省略，见 01 算法）。
- ⚠️ **最容易搞错**：注入顺序与覆盖优先级。必须「构造 → 字段 → (属性) → 方法」，方法注入放最后以便依赖已注入成员；覆盖参数匹配是**列表顺序先到先得**，没有 typed/named 谁优先的硬规则——仿写时若调换顺序会得到不同注入结果。
