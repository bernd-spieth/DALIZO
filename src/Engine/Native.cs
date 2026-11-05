// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Runtime.InteropServices;
internal static class N {
const string Obj="libgobject-2.0-0.dll", Glib="libglib-2.0-0.dll", Spice="libspice-client-glib-2.0-8.dll";
[StructLayout(LayoutKind.Sequential)] internal struct PrimarySurface { public int Format, Width, Height, Stride, ShmId; public IntPtr Data; public int Marked; }
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern int spice_main_agent_test_capability(IntPtr channel, uint capability);
    [DllImport("libspice-client-glib-2.0-8.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int spice_display_get_primary(IntPtr channel, uint surfaceId, out PrimarySurface surface);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetDllDirectory(string path);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong g_signal_connect_data(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string signal, IntPtr callback, IntPtr data, IntPtr destroy, int flags);
    [DllImport(Obj, EntryPoint = "g_object_set", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetString(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_object_set", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetInt(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string key, int value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_object_set", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetPtr(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string key, IntPtr value, IntPtr end);
    [DllImport(Obj, EntryPoint = "g_object_get", CallingConvention = CallingConvention.Cdecl)] internal static extern void GetInt(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string key, out int value, IntPtr end);
    [DllImport(Obj, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_object_unref(IntPtr obj);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_byte_array_new();
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_byte_array_append(IntPtr array, byte[] bytes, uint count);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_byte_array_unref(IntPtr array);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr spice_session_new();
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern int spice_session_connect(IntPtr session);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_session_disconnect(IntPtr session);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_inputs_key_press(IntPtr channel, uint scancode);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_inputs_key_release(IntPtr channel, uint scancode);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_inputs_position(IntPtr channel, int x, int y, int displayId, int buttons);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_inputs_motion(IntPtr channel, int dx, int dy, int buttons);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_inputs_button_press(IntPtr channel, int button, int buttons);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_inputs_button_release(IntPtr channel, int button, int buttons);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_main_request_mouse_mode(IntPtr channel, int mode);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_main_update_display(IntPtr channel,int id,int x,int y,int width,int height,int update);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern void spice_main_update_display_enabled(IntPtr channel,int id,int enabled,int update);
    [DllImport(Spice, CallingConvention = CallingConvention.Cdecl)] internal static extern int spice_main_send_monitor_config(IntPtr channel);
[DllImport(Glib, CallingConvention=CallingConvention.Cdecl)] internal static extern int g_main_context_iteration(IntPtr context,int block);
[DllImport(Glib, CallingConvention=CallingConvention.Cdecl)] internal static extern int g_main_context_pending(IntPtr context);
[DllImport(Spice, CallingConvention=CallingConvention.Cdecl)] internal static extern int spice_channel_connect(IntPtr channel);
[DllImport(Obj, CallingConvention=CallingConvention.Cdecl)] internal static extern void g_signal_handler_disconnect(IntPtr obj,ulong handler);
[DllImport(Obj, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr g_object_ref(IntPtr obj);
}
