using AiNative.Application.Agents;
using AiNative.Infrastructure.Common;

namespace AiNative.Infrastructure.Agents;

/// <summary>
/// 通知心跳调度器有新的唤醒。
/// </summary>
internal sealed class WakeupSignal : CoalescingSignal, IWakeupSignal;
