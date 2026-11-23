using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FlashTrans.Services;

namespace FlashTrans.Interop;

/// <summary>仅包含业务需要的 WinRT ABI；槽位依据 Windows SDK 的 winrt 头文件。</summary>
internal static unsafe class NativeWinRt
{
    internal static nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    internal static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    internal sealed class Apartment : IDisposable
    {
        readonly bool _initialized;
        public Apartment()
        {
            var hr = RoInitialize(1); // RO_INIT_MULTITHREADED
            // WPF 的 STA 已初始化 COM；同步元数据查询沿用该 apartment，不尝试切线程模型。
            if (hr == unchecked((int)0x80010106)) return; // RPC_E_CHANGED_MODE
            Check(hr);
            _initialized = true;
        }
        public void Dispose() { if (_initialized) RoUninitialize(); }
    }

    internal sealed class HString : IDisposable
    {
        public nint Pointer { get; private set; }
        public HString(string text)
        {
            Check(WindowsCreateString(text, (uint)text.Length, out var value));
            Pointer = value;
        }
        public void Dispose() { WindowsDeleteString(Pointer); Pointer = 0; }
    }

    internal static string ConsumeString(nint value)
    {
        try
        {
            var buffer = WindowsGetStringRawBuffer(value, out var length);
            return new string((char*)buffer, 0, checked((int)length));
        }
        finally { WindowsDeleteString(value); }
    }

    internal static NativeCom Factory(string runtimeClass, Guid iid)
    {
        using var name = new HString(runtimeClass);
        Check(RoGetActivationFactory(name.Pointer, in iid, out var factory));
        return new NativeCom(factory);
    }

    internal static NativeCom Activate(string runtimeClass, Guid iid)
    {
        using var name = new HString(runtimeClass);
        Check(RoActivateInstance(name.Pointer, out var instance));
        using var inspectable = new NativeCom(instance);
        return inspectable.Query(iid, close: true);
    }

    [DllImport("combase.dll")] static extern int RoInitialize(uint initType);
    [DllImport("combase.dll")] static extern void RoUninitialize();
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("combase.dll")] static extern int RoActivateInstance(nint name, out nint instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string source, uint length, out nint value);
    [DllImport("combase.dll")] static extern nint WindowsGetStringRawBuffer(nint value, out uint length);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint value);
}

/// <summary>一个拥有独立引用的 COM 指针；确定性释放，最终器只负责兜底 Release。</summary>
internal sealed unsafe class NativeCom : SafeHandleZeroOrMinusOneIsInvalid
{
    readonly bool _close;
    internal nint Pointer => !IsClosed ? handle : throw new ObjectDisposedException(nameof(NativeCom));

    internal NativeCom(nint ownedPointer, bool close = false) : base(true)
    {
        if (ownedPointer == 0) throw new ArgumentNullException(nameof(ownedPointer));
        SetHandle(ownedPointer);
        _close = close;
    }

    internal NativeCom Query(Guid iid, bool close = false)
    {
        nint result = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)NativeWinRt.Slot(Pointer, 0))(Pointer, &iid, &result));
        return new NativeCom(result, close);
    }

    internal NativeCom GetObject(int slot, bool close = false)
    {
        nint result = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)NativeWinRt.Slot(Pointer, slot))(Pointer, &result));
        return new NativeCom(result, close);
    }

    internal string GetString(int slot)
    {
        nint result = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)NativeWinRt.Slot(Pointer, slot))(Pointer, &result));
        return NativeWinRt.ConsumeString(result);
    }

    internal uint GetUInt32(int slot)
    {
        uint result = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)NativeWinRt.Slot(Pointer, slot))(Pointer, &result));
        return result;
    }

    internal NativeCom GetAt(uint index)
    {
        nint result = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)NativeWinRt.Slot(Pointer, 6))(Pointer, index, &result));
        return new NativeCom(result);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _close && !IsClosed && !IsInvalid)
        {
            var iid = new Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e"); // IClosable
            nint closable = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)NativeWinRt.Slot(handle, 0))(handle, &iid, &closable);
            if (hr >= 0)
            {
                try
                {
                    hr = ((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(closable, 6))(closable);
                    if (hr < 0) Log.Warn($"关闭 WinRT 资源失败：0x{hr:X8}");
                }
                finally { Marshal.Release(closable); }
            }
        }
        base.Dispose(disposing);
    }

    protected override bool ReleaseHandle() { Marshal.Release(handle); return true; }
}

// WinRT 委托继承 IUnknown，Invoke 在槽 3；泛型 IID 直接取 SDK 头文件。
[ComVisible(true), Guid("989c1371-444a-5e7e-b197-9eaaf9d2829a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOcrCompletedHandler { [PreserveSig] int Invoke(nint operation, int status); }
[ComVisible(true), Guid("c972b996-6165-50d4-af60-a8c3df51d092"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISpeechCompletedHandler { [PreserveSig] int Invoke(nint operation, int status); }
[ComVisible(true), Guid("06386a7a-e009-5b0b-ab68-a8e48b516647"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IBufferCompletedHandler { [PreserveSig] int Invoke(nint operation, int status); }
[ComVisible(true), Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeAgileObject { }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeCompletedHandler : IOcrCompletedHandler, ISpeechCompletedHandler, IBufferCompletedHandler, INativeAgileObject
{
    internal readonly TaskCompletionSource<bool> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Invoke(nint operation, int status) { Finished.TrySetResult(true); return 0; }
}

internal static unsafe class NativeAsync
{
    internal static NativeCom Wait(NativeCom operation, Type handlerType, CancellationToken ct, bool withProgress = false)
    {
        // 异步操作的生命周期留在同一个 MTA 工作线程；取消后也等完成回调，
        // 避免底层 OCR 仍访问已释放的 SoftwareBitmap，或读流仍写已释放的 IBuffer。
        using var info = operation.Query(new Guid("00000036-0000-0000-c000-000000000046"));
        var listener = new NativeCompletedHandler();
        var callback = Marshal.GetComInterfaceForObject(listener, handlerType);
        try
        {
            var putCompleted = withProgress ? 8 : 6;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)NativeWinRt.Slot(operation.Pointer, putCompleted))(operation.Pointer, callback));
            using (ct.Register(() =>
            {
                var hr = ((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(info.Pointer, 9))(info.Pointer);
                if (hr < 0) Log.Warn($"取消 WinRT 操作失败：0x{hr:X8}");
            }))
            {
                listener.Finished.Task.GetAwaiter().GetResult();
            }
            ct.ThrowIfCancellationRequested();
            if (info.GetUInt32(7) == 2) throw new OperationCanceledException("系统操作已取消。", ct);
            nint result = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)NativeWinRt.Slot(operation.Pointer, withProgress ? 10 : 8))(operation.Pointer, &result));
            return new NativeCom(result);
        }
        finally
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(info.Pointer, 10))(info.Pointer);
            if (hr < 0) Log.Warn($"关闭 WinRT 异步操作失败：0x{hr:X8}");
            Marshal.Release(callback);
            // 不在回调刚发信号时释放其同步对象，避免与尚未返回的 Invoke 竞争。
            // CCW 的引用由 COM 和本地 callback 各自释放，完成任务由 CLR 管理。
        }
    }
}
