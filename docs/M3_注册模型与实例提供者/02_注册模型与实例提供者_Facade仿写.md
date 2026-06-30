# M3 注册模型与实例提供者 · Facade 仿写

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| `Registration` 不可变记录 | 保留 | **保留**（核心不变量） |
| `IInstanceProvider` 策略族 | 保留 Instance/Existing/Func/Collection | **保留** |
| `RegistrationBuilder` 流式 + 子类 Build 重写 | 保留 As/AsSelf + Build 虚方法 | **保留** |
| `Registry` 用 `FixedTypeObjectKeyHashtable` | 退化为 `Dictionary<(Type,object),...>` | **简化**（语义等价） |
| 集合自动派生 + AnyKey 追踪 | 保留「同 service≥2 → 合成集合」 | **保留**（最有价值） |
| 集合跨作用域聚合 | 简化为单作用域聚合 | **简化**（作用域树见 M4） |
| 开放泛型回退 | 保留最小 `MakeGenericType` 版 | **保留** |
| `ContainerLocal` / Component / ECS Provider | 砍掉 | **砍掉** |
| `As` 的可赋值校验 | 保留 | **保留** |

## 最小可编译复刻

```csharp
using System;
using System.Collections;
using System.Collections.Generic;

namespace MiniVC.Registration
{
    public enum Lifetime { Transient, Singleton, Scoped }

    public interface IObjectResolver { object Resolve(Type type, object key = null); object Resolve(Registration reg); }
    public interface IInstanceProvider { object SpawnInstance(IObjectResolver r); }

    public sealed class Registration
    {
        public readonly Type ImplementationType;
        public readonly IReadOnlyList<Type> InterfaceTypes;
        public readonly Lifetime Lifetime;
        public readonly IInstanceProvider Provider;
        public readonly object Key;
        public Registration(Type impl, Lifetime life, IReadOnlyList<Type> ifaces, IInstanceProvider p, object key = null)
        { ImplementationType = impl; Lifetime = life; InterfaceTypes = ifaces; Provider = p; Key = key; }
        public object SpawnInstance(IObjectResolver r) => Provider.SpawnInstance(r);
    }

    // ---- Providers ----
    public sealed class ExistingInstanceProvider : IInstanceProvider
    { readonly object inst; public ExistingInstanceProvider(object i) => inst = i; public object SpawnInstance(IObjectResolver _) => inst; }

    public sealed class FuncInstanceProvider : IInstanceProvider
    { readonly Func<IObjectResolver, object> f; public FuncInstanceProvider(Func<IObjectResolver, object> f) => this.f = f; public object SpawnInstance(IObjectResolver r) => f(r); }

    public sealed class DelegateInstanceProvider : IInstanceProvider   // 代替 InstanceProvider+IInjector（M2 已示范注入）
    { readonly Func<IObjectResolver, object> ctor; public DelegateInstanceProvider(Func<IObjectResolver, object> c) => ctor = c; public object SpawnInstance(IObjectResolver r) => ctor(r); }

    public sealed class CollectionInstanceProvider : IInstanceProvider, IEnumerable<Registration>
    {
        public readonly Type ElementType;
        readonly List<Registration> regs = new();
        public CollectionInstanceProvider(Type elem) => ElementType = elem;
        public void Add(Registration r) => regs.Add(r);
        public IEnumerator<Registration> GetEnumerator() => regs.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public object SpawnInstance(IObjectResolver r)
        {
            var arr = Array.CreateInstance(ElementType, regs.Count);
            for (var i = 0; i < regs.Count; i++) arr.SetValue(r.Resolve(regs[i]), i);
            return arr;
        }
    }

    public sealed class OpenGenericInstanceProvider : IInstanceProvider
    {
        readonly Type openImpl; readonly Lifetime life;
        readonly Dictionary<string, Registration> cache = new();
        public OpenGenericInstanceProvider(Type openImpl, Lifetime life) { this.openImpl = openImpl; this.life = life; }
        public Registration GetClosed(Type[] typeArgs)
        {
            var k = string.Join(",", Array.ConvertAll(typeArgs, t => t.FullName));
            if (!cache.TryGetValue(k, out var reg))
            {
                var closed = openImpl.MakeGenericType(typeArgs);
                reg = new Registration(closed, life, new[] { closed },
                    new DelegateInstanceProvider(r => Activator.CreateInstance(closed)));
                cache[k] = reg;
            }
            return reg;
        }
        public object SpawnInstance(IObjectResolver _) => throw new InvalidOperationException("open generic does not spawn");
    }

    // ---- Builder ----
    public class RegistrationBuilder
    {
        protected internal readonly Type ImplementationType;
        protected internal readonly Lifetime Lifetime;
        protected internal List<Type> InterfaceTypes;
        protected internal object Key;
        public RegistrationBuilder(Type impl, Lifetime life) { ImplementationType = impl; Lifetime = life; }

        public RegistrationBuilder As(Type iface)
        {
            if (!iface.IsAssignableFrom(ImplementationType))
                throw new InvalidOperationException($"{ImplementationType} not assignable to {iface}");
            (InterfaceTypes ??= new List<Type>()).Add(iface);
            return this;
        }
        public RegistrationBuilder AsSelf() => As(ImplementationType);
        public RegistrationBuilder Keyed(object key) { Key = key; return this; }

        public virtual Registration Build() =>
            new Registration(ImplementationType, Lifetime, InterfaceTypes,
                new DelegateInstanceProvider(r => Activator.CreateInstance(ImplementationType)), Key);
    }

    // ---- Registry ----
    public sealed class Registry
    {
        static readonly object AnyKey = new();
        readonly Dictionary<(Type, object), Registration> table = new();

        public static Registry Build(IEnumerable<Registration> registrations)
        {
            var buf = new Dictionary<(Type, object), Registration>();
            foreach (var reg in registrations)
            {
                if (reg.InterfaceTypes != null)
                    foreach (var svc in reg.InterfaceTypes) AddToBuffer(buf, svc, reg);
                else
                    AddToBuffer(buf, reg.ImplementationType, reg);
            }
            return new Registry(buf);
        }
        Registry(Dictionary<(Type, object), Registration> buf) => table = buf;

        static void AddToBuffer(Dictionary<(Type, object), Registration> buf, Type svc, Registration reg)
        {
            var key = (svc, reg.Key);
            var anyKey = (svc, AnyKey);
            if (buf.TryGetValue(anyKey, out var exists))           // 已有同 service → 合成集合
            {
                CollectionInstanceProvider coll;
                var enumKey = (typeof(IEnumerable<>).MakeGenericType(svc), (object)null);
                if (buf.TryGetValue(enumKey, out var found) && found.Provider is CollectionInstanceProvider c) coll = c;
                else
                {
                    coll = new CollectionInstanceProvider(svc) ; coll.Add(exists);
                    var collReg = new Registration(svc.MakeArrayType(), Lifetime.Transient,
                        new[] { typeof(IEnumerable<>).MakeGenericType(svc), typeof(IReadOnlyList<>).MakeGenericType(svc) }, coll);
                    foreach (var t in collReg.InterfaceTypes) buf[(t, null)] = collReg;
                }
                coll.Add(reg);
                buf[anyKey] = reg; buf[key] = reg;                 // 单值键被后者覆盖
            }
            else { buf[key] = reg; buf[anyKey] = reg; }
        }

        public bool TryGet(Type type, object key, out Registration reg)
        {
            if (table.TryGetValue((type, key), out reg)) return true;
            if (type.IsConstructedGenericType)                     // 开放泛型回退
            {
                var open = type.GetGenericTypeDefinition();
                if (table.TryGetValue((open, key), out var openReg) &&
                    openReg.Provider is OpenGenericInstanceProvider ogp)
                { reg = ogp.GetClosed(type.GetGenericArguments()); return true; }
            }
            reg = null; return false;
        }
    }
}
```

## 使用示例

```csharp
var regs = new List<Registration>
{
    new RegistrationBuilder(typeof(FooA), Lifetime.Singleton).As(typeof(IFoo)).Build(),
    new RegistrationBuilder(typeof(FooB), Lifetime.Singleton).As(typeof(IFoo)).Build(),  // 第2个 → 触发集合
};
var registry = Registry.Build(regs);

registry.TryGet(typeof(IFoo), null, out var single);          // → FooB（后者覆盖）
registry.TryGet(typeof(IEnumerable<IFoo>), null, out var all); // → 集合注册（FooA + FooB）
```

## 取舍自检

- ✅ **保留**：Registration 不可变、Provider 策略族、集合自动派生（AnyKey 追踪 + 单值/集合双轨）、开放泛型「Provider 不产实例、即时合成封闭注册并缓存」、`As` 可赋值校验。
- ❌ **砍掉**：跨作用域集合聚合（属 M4 作用域树）、`ContainerLocal`/Component/ECS Provider、`FixedTypeObjectKeyHashtable`（用 Dictionary）、并行 Build、循环依赖检测。
- ⚠️ **最容易搞错**：集合的「单值键被覆盖 vs 集合键累积」双轨。漏掉 `buf[key]=reg` 的覆盖，`Resolve<IFoo>()` 会拿到第一个而非最后一个；漏掉集合派生，`Resolve<IEnumerable<IFoo>>()` 直接 miss。两套映射必须在同一次 `AddToBuffer` 内一起维护。
