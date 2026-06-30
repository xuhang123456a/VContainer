# M4 容器解析与作用域树 · Facade 仿写

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| `Container` + 内嵌 `rootScope` 双实现 | 合并为单一 `Container`(parent 可空) | **简化**（语义保留） |
| `ConcurrentDictionary<Registration,Lazy>` | 保留 | **保留**（核心：构造一次） |
| 三种 Lifetime 解析 | 保留全部 | **保留** |
| 单例归属层级路由 | 保留「本层有则本层建、否则上抛」 | **保留**（最有价值） |
| Dispose 三重守卫 | 保留 | **保留** |
| `CompositeDisposable` LIFO | 复用 M1 | **保留** |
| `CreateScope` + 子 Builder | 保留最小版 | **保留** |
| Diagnostics 旁路 | 砍掉 | **砍掉** |
| 并行 Build / ApplicationOrigin | 砍掉 | **砍掉** |
| `IObjectResolver` 自注册 | 保留 | **保留** |

## 最小可编译复刻

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MiniVC.Registration;   // 复用 M3 的 Registration/Registry/Lifetime/IInstanceProvider
using MiniVC.Foundation;     // 复用 M1 的 CompositeDisposable

namespace MiniVC.Resolution
{
    public sealed class Container : IObjectResolver, IDisposable
    {
        readonly Registry registry;
        readonly Container parent;       // null = 根
        readonly Container root;
        readonly ConcurrentDictionary<Registration, Lazy<object>> shared = new();
        readonly CompositeDisposable disposables = new();
        readonly Func<Registration, Lazy<object>> factory;

        public Container(Registry registry, Container parent = null)
        {
            this.registry = registry;
            this.parent = parent;
            root = parent?.root ?? this;
            factory = reg => new Lazy<object>(() => reg.SpawnInstance(this));
        }

        public object Resolve(Type type, object key = null)
        {
            if (TryFindRegistration(type, key, out var reg)) return Resolve(reg);
            throw new InvalidOperationException($"No registration for {type}{(key == null ? "" : $" key={key}")}");
        }

        public object Resolve(Registration reg)
        {
            switch (reg.Lifetime)
            {
                case Lifetime.Transient:
                    return reg.SpawnInstance(this);             // 不跟踪 Dispose

                case Lifetime.Singleton:
                    if (parent == null) return Track(reg);      // 根：本地缓存
                    if (registry.TryGet(reg.ImplementationType, reg.Key, out _))
                        return Track(reg);                      // 本作用域声明 → 本层单例
                    return parent.Resolve(reg);                 // 否则向上路由到拥有者

                case Lifetime.Scoped:
                    return Track(reg);                          // 当前作用域缓存

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        object Track(Registration reg)
        {
            var lazy = shared.GetOrAdd(reg, factory);
            var created = lazy.IsValueCreated;                  // 取值前快照
            var instance = lazy.Value;
            if (!created && instance is IDisposable d && !(reg.Provider is ExistingInstanceProvider))
                disposables.Add(d);                             // 三重守卫
            return instance;
        }

        bool TryFindRegistration(Type type, object key, out Registration reg)
        {
            for (Container c = this; c != null; c = c.parent)
                if (c.registry.TryGet(type, key, out reg)) return true;
            reg = null; return false;
        }

        public Container CreateScope(Action<ContainerBuilder> install = null)
        {
            var b = new ContainerBuilder();
            install?.Invoke(b);
            return b.BuildScope(root, this);
        }

        public void Inject(object instance) { /* 调 M2: InjectorCache.GetOrBuild(t).Inject(instance,this,null) */ }

        public void Dispose()
        {
            disposables.Dispose();   // LIFO
            shared.Clear();
        }
    }

    public interface IObjectResolver
    {
        object Resolve(Type type, object key = null);
        object Resolve(Registration reg);
        Container CreateScope(Action<ContainerBuilder> install = null);
        void Inject(object instance);
    }

    public sealed class ContainerBuilder
    {
        readonly List<Registration> regs = new();
        Action<IObjectResolver> buildCallback;

        public void Register(Registration reg) => regs.Add(reg);
        public void RegisterBuildCallback(Action<IObjectResolver> cb) => buildCallback += cb;

        public Container Build()
        {
            var registry = Registry.Build(regs);
            var c = new Container(registry);
            buildCallback?.Invoke(c);          // 构建回调注入点
            return c;
        }

        public Container BuildScope(Container root, Container parent)
        {
            var registry = Registry.Build(regs);
            var c = new Container(registry, parent);
            buildCallback?.Invoke(c);
            return c;
        }
    }
}
```

> 注：为聚焦核心，本 Facade 把 `Container` 与 `ScopedContainer` 合一（用 `parent==null` 区分根）。原版分两类是为各自独立的 `sharedInstances`/`disposables` 与 `IScopedObjectResolver` 接口；合一不改变生命周期路由的核心不变量。

## 使用示例

```csharp
var builder = new ContainerBuilder();
builder.Register(new RegistrationBuilder(typeof(Logger), Lifetime.Singleton).AsSelf().Build());
var root = builder.Build();

var a = root.Resolve(typeof(Logger));

using var scope = root.CreateScope(b =>
    b.Register(new RegistrationBuilder(typeof(RequestCtx), Lifetime.Scoped).AsSelf().Build()));

var ctx1 = scope.Resolve(typeof(RequestCtx));
var ctx2 = scope.Resolve(typeof(RequestCtx));   // 同一对象（Scoped 缓存）
var loggerInScope = scope.Resolve(typeof(Logger)); // == a（单例向上路由到 root）
```

## 取舍自检

- ✅ **保留**：`Lazy<object>` 保证构造一次、单例归属层级路由、Dispose 三重守卫 + LIFO、`Resolve(Type)`/`Resolve(Registration)` 双语义、`CreateScope` 父链、`IObjectResolver` 自注册。
- ❌ **砍掉**：Container/ScopedContainer 双实现、Diagnostics 旁路、并行 Build、`ApplicationOrigin`、`TryResolve`/`ResolveOrDefault` 等门面糖。
- ⚠️ **最容易搞错**：单例 Dispose 登记的"取值前快照 `created`"。如果在 `lazy.Value` 之后才读 `IsValueCreated`，它永远是 true，导致**首次创建的可弃单例不被登记**，容器 Dispose 时漏释放。必须在取 `Value` 之前抓 `created` 快照。
