# M7 诊断可观测层 · Facade 仿写

> 说明：复刻"旁路采集器 + `?.` 零成本开关 + 解析依赖树重建 + 按生命周期聚合耗时"。用抽象 `Registration`/`Lifetime` 占位（复用 M3 语义）。

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| `Diagnostics?.Trace*` 可空开关 | 保留 | **保留**（核心不变量） |
| `TraceResolve(reg, Func)` 委托透传 | 保留 | **保留** |
| ThreadLocal 调用栈重建依赖树 | 保留 | **保留**（最有价值） |
| 按生命周期聚合耗时（均值/最大） | 保留 | **保留** |
| `RegisterInfo` StackTrace 源码定位 | 退化为存调用方字符串 | **简化** |
| 全局 `DiagnositcsContext` 多作用域 | 退化为单 Collector | **简化** |
| `GetGroupedDiagnosticsInfos`/事件 | 砍掉 | **砍掉** |
| 集合 Provider 跳过 / Instances 去重 | 保留 | **保留** |

## 最小可编译复刻

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MiniVC.Diagnostics
{
    public enum Lifetime { Transient, Singleton, Scoped }
    public sealed class Registration { public Lifetime Lifetime; public Type Type; public bool IsCollection; }

    public sealed class ResolveInfo
    {
        public readonly Registration Registration;
        public readonly List<object> Instances = new();
        public int MaxDepth = -1;
        public int RefCount;
        public long ResolveTime;
        public ResolveInfo(Registration r) => Registration = r;
    }

    public sealed class DiagnosticsInfo
    {
        public readonly string Headline;            // 等价 RegisterInfo 源码定位
        public ResolveInfo ResolveInfo;
        public readonly List<DiagnosticsInfo> Dependencies = new();
        public DiagnosticsInfo(string headline, Registration reg)
        { Headline = headline; ResolveInfo = new ResolveInfo(reg); }
    }

    public sealed class DiagnosticsCollector
    {
        readonly List<DiagnosticsInfo> infos = new();
        readonly ThreadLocal<Stack<DiagnosticsInfo>> callStack = new(() => new Stack<DiagnosticsInfo>());

        // 注册态：抓调用方位置
        public void TraceRegister(Registration reg)
        {
            var headline = new StackTrace(true).GetFrame(1)?.GetMethod()?.Name ?? "?";
            infos.Add(new DiagnosticsInfo(headline, reg));
        }

        DiagnosticsInfo Find(Registration reg) => infos.Find(x => x.ResolveInfo.Registration == reg);

        // 解析态：委托透传 + 统计 + 依赖树
        public object TraceResolve(Registration reg, Func<Registration, object> resolving)
        {
            var current = Find(reg);
            var owner = callStack.Value.Count > 0 ? callStack.Value.Peek() : null;

            if (reg.IsCollection || current == null || current == owner)
                return resolving(reg);                          // 跳过噪声/自引用

            current.ResolveInfo.RefCount++;
            current.ResolveInfo.MaxDepth = current.ResolveInfo.MaxDepth < 0
                ? callStack.Value.Count
                : Math.Max(current.ResolveInfo.MaxDepth, callStack.Value.Count);
            owner?.Dependencies.Add(current);                   // 织入依赖树

            callStack.Value.Push(current);
            var sw = Stopwatch.StartNew();
            var instance = resolving(reg);                      // 实际解析
            sw.Stop();
            callStack.Value.Pop();

            SetResolveTime(current, sw.ElapsedMilliseconds);
            if (!current.ResolveInfo.Instances.Contains(instance))
                current.ResolveInfo.Instances.Add(instance);
            return instance;
        }

        static void SetResolveTime(DiagnosticsInfo info, long elapsed)
        {
            var ri = info.ResolveInfo;
            ri.ResolveTime = ri.Registration.Lifetime == Lifetime.Transient
                ? (ri.ResolveTime * (ri.RefCount - 1) + elapsed) / ri.RefCount   // 移动平均
                : Math.Max(ri.ResolveTime, elapsed);                              // 单例/Scoped 取最大
        }

        public IReadOnlyList<DiagnosticsInfo> Snapshot() => infos;
    }

    // 模拟容器解析点（核心：?. 开关 + 委托透传）
    public sealed class MiniContainer
    {
        public DiagnosticsCollector Diagnostics;   // null = 关闭，零开销
        object ResolveCore(Registration reg) { /* 实际构造 */ return new object(); }
        public object Resolve(Registration reg)
            => Diagnostics != null
                ? Diagnostics.TraceResolve(reg, ResolveCore)   // 旁路织入
                : ResolveCore(reg);                            // 短路零成本
    }
}
```

## 使用示例

```csharp
var c = new MiniContainer { Diagnostics = new DiagnosticsCollector() };
var regA = new Registration { Type = typeof(string), Lifetime = Lifetime.Singleton };
c.Diagnostics.TraceRegister(regA);

c.Resolve(regA);
c.Resolve(regA);   // RefCount=2，Singleton 取最大耗时，Instances 去重为 1

foreach (var info in c.Diagnostics.Snapshot())
    Console.WriteLine($"{info.Headline}: refs={info.ResolveInfo.RefCount}, deps={info.Dependencies.Count}");

// 关闭诊断：Diagnostics=null → Resolve 走 ResolveCore，无任何采集开销
```

## 取舍自检

- ✅ **保留**：`?.` 可空开关（关闭零成本）、`TraceResolve` 委托透传保证行为等价、ThreadLocal 调用栈重建依赖树、按生命周期聚合耗时（Transient 均值 / 单例最大）、集合跳过 + Instances 去重。
- ❌ **砍掉**：真实 StackTrace 源码定位（退化为方法名）、多作用域全局表、分组/过滤视图、OnContainerBuilt 事件、TraceBuild 的 builder→reg 回填（合并进 TraceRegister）。
- ⚠️ **最容易搞错**：`TraceResolve` 必须**把真实解析逻辑作为委托透传**，且开启/关闭路径的返回结果完全一致。若在采集分支里改了构造方式（如缓存策略不同），就违反"观测不改变行为"。另外 `MaxDepth` 的首次赋值用 `< 0` 判初值，漏掉会让顶层项过滤（`MaxDepth<=1`）出错。
