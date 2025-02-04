﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace InteropMgr
{
    public class Injector
    {
        private readonly Target target;
        public IntPtr LibraryHandle { get; private set; } = IntPtr.Zero;
        private string dllName = string.Empty;
        public Injector(Target target)
        {
            this.target = target;
        }
        public void Inject(string dllName)
        {
            Inject(dllName, false);
        }
        /// <summary>
        ///
        /// </summary>
        /// <param name="dllName">name of the dll we want to inject</param>
        public void Inject(string dllName, bool printDebugInfo)
        {
            //ich hab keine ahnung was hier passiert
            target.Assertions.AssertProcessAttached();
            target.Assertions.AssertInjectionPermissions();

            //hier wird irgendwas potenziell gefährliches geprüft
            if (Process.GetCurrentProcess().IsWin64Emulator() != target.Is32BitProcess)
            {
                HelperMethods.Debug("Warning: trying to access 64-bit process from 32-bit process or the other way around.", printDebugInfo);
            }
            
            // searching for the address of LoadLibraryA and storing it in a pointer
            //das ist eine bibliothekt um dlls irgenwohin zu laden, die ist in der kernel32.dll gespeichert, aus irgendeinem grund muss sie aber gesucht werden, versteh ich nicht
            IntPtr kernel32Handle = WinAPI.GetModuleHandle("kernel32.dll");
            if (kernel32Handle == IntPtr.Zero)
            {
                uint errorCode = WinAPI.GetLastError();
                throw new Win32Exception((int)errorCode, "Encountered error " + errorCode.ToString() + " (0x" + errorCode.ToString("x") + ") - FATAL: Could not get handle of kernel32.dll: was NULL.");
            }
            IntPtr loadLibraryAddr = WinAPI.GetProcAddress(kernel32Handle, "LoadLibraryA");
            if (loadLibraryAddr == IntPtr.Zero)
            {
                uint errorCode = WinAPI.GetLastError();
                throw new Win32Exception((int)errorCode, "Encountered error " + errorCode.ToString() + " (0x" + errorCode.ToString("x") + ") - FATAL: Could not get address of LoadLibraryA: was NULL.");
            }
            HelperMethods.Debug("LoadLibraryA is at 0x" + loadLibraryAddr.ToInt64().ToString("x"), printDebugInfo);
            //Anscheinend sucht man den kernel32/loadlibrary prozess, um herauszufinden, wo man den ansprechen kann?? und dabei kann es zu einigen fehlern kommen.

            // alocating some memory on the target process - enough to store the name of the dll
            // and storing its address in a pointer
            // was ist unmanaged und managed code?
            uint size = (uint)((dllName.Length + 1) * Marshal.SizeOf(typeof(char)));
            IntPtr allocMemAddress = WinAPI.VirtualAllocEx(target.Handle, IntPtr.Zero, size, (uint)Permissions.MemoryPermission.MEM_COMMIT | (uint)Permissions.MemoryPermission.MEM_RESERVE, (uint)Permissions.MemoryPermission.PAGE_READWRITE); //what
            HelperMethods.Debug("Allocated memory at 0x" + allocMemAddress.ToInt64().ToString("x"), printDebugInfo);
            //jetzt wurde anscheinen speciher reserviert

            int bytesWritten = 0;
            // writing the name of the dll there
            //jetzt schreibt man den namen der dll an den ort, wo man hin will... wo dieser ort aber eigentlihc ist ist nicht ganz klar. das ist dann der target prozess, oder? ja genua man übergibt ja die dll und ein target objekt
            //jetzt kommt irgendwie höhere mathemaitk
            byte[] buffer = new byte[size];
            byte[] bytes = Encoding.ASCII.GetBytes(dllName);
            Array.Copy(bytes, 0, buffer, 0, bytes.Length);
            buffer[buffer.Length - 1] = 0;
            //schreibt die dll an den Ort, ort unklar
            bool success = WinAPI.WriteProcessMemory((uint)target.Handle, target.Is32BitProcess ? allocMemAddress.ToInt32() : allocMemAddress.ToInt64(), buffer, size, ref bytesWritten);
            //schaut ob das geklappt hat
            if (success)
            {
                HelperMethods.Debug("Successfully wrote \"" + dllName + "\" to 0x" + allocMemAddress.ToInt64().ToString("x"), printDebugInfo);
            }
            else
            {
                HelperMethods.Debug("FAILED to write dll name!", printDebugInfo);
            }



            // creating a thread that will call LoadLibraryA with allocMemAddress as argument
            // jetzt will man aber schon wieder was injecten. ich dachte man hat schon was injected. Oder war das nur der name? braucht man beides. warum braucht man den namen?
            //macht einen "thread" auf. Was ist das? der prozess der die dll injected
            HelperMethods.Debug("Injecting dll ...", printDebugInfo);
            IntPtr threadHandle = WinAPI.CreateRemoteThread(target.Handle, IntPtr.Zero, 0, loadLibraryAddr, allocMemAddress, 0, out _);
            HelperMethods.Debug("CreateRemoteThread returned the following handle: 0x" + threadHandle.ToInt32().ToString("x"), printDebugInfo);
            uint errCode = WinAPI.GetLastError();

            // das kann anscheinend furchtbar schief gehen
            if (threadHandle == IntPtr.Zero)
            {
                throw new Win32Exception((int)errCode, "Encountered error " + errCode.ToString() + " (0x" + errCode.ToString("x") + ") - FATAL: CreateRemoteThread returned NULL pointer as handle.");
            }
            HelperMethods.Debug("CreateRemoteThread threw errorCode 0x" + errCode.ToString("x"), printDebugInfo);

            // jetzt wartet man 10 sekundenn auf irgenwas
            uint waitExitCode = WinAPI.WaitForSingleObject(threadHandle, 10 * 1000);
            HelperMethods.Debug("Waiting for thread to exit ...", printDebugInfo);
            HelperMethods.Debug("WaitForSingleObject returned 0x" + waitExitCode.ToString("x"), printDebugInfo);

            //weitere interssante dinge werden gemacht
            success = WinAPI.GetExitCodeThread(threadHandle, out IntPtr moduleHandle);

            //was ist das?
            LibraryHandle = moduleHandle;
            if (!success)
            {
                uint errorCode = WinAPI.GetLastError();
                throw new Win32Exception((int)errorCode, "Encountered error " + errorCode.ToString() + " (0x" + errorCode.ToString("x") + ") - FATAL: Non-zero exit code of GetExitCodeThread.");
            }

            HelperMethods.Debug("Currently the following modules are LOADED:", printDebugInfo);
            foreach (ProcessModule module in target.Process.Modules)
            {
                HelperMethods.Debug("  - " + module.FileName, printDebugInfo);
            }



            HelperMethods.Debug("Remote thread returned 0x" + LibraryHandle.ToInt64().ToString("x"), printDebugInfo);
            //jetzt macht man irgenwas wieder zu und je nachdem wie erfolgreich das war wird etwas gemacht
            success = WinAPI.CloseHandle(threadHandle);
            if (!success)
            {
                uint errorCode = WinAPI.GetLastError();
                throw new Win32Exception((int)errorCode, "Encountered error " + errorCode.ToString() + " (0x" + errorCode.ToString("x") + ") - FATAL: Failed calling CloseHandle on 0x" + threadHandle.ToInt64().ToString("x") + ".");
            }
            HelperMethods.Debug("Called CloseHandle on 0x" + threadHandle.ToInt64().ToString("x") + ".", printDebugInfo);
            
            
            //jetzt wird ganz wild. also das räumt irgendwelche virtuellen speicheradressen wieder auf. und kann anscheinen auch schief gehen
            success = WinAPI.VirtualFreeEx(target.Handle, allocMemAddress, 0, 0x8000);
            if (!success)
            {
                uint errorCode = WinAPI.GetLastError();
                throw new Win32Exception((int)errorCode, "Encountered error " + errorCode.ToString() + " (0x" + errorCode.ToString("x") + ") - FATAL: Failed calling VirtualFreeEx on 0x" + allocMemAddress.ToInt64().ToString("x") + ".");
            }
            
            //also anscheinen hat man jetzt bereits die dll injected. aber ich hab keine ahnung wann eigentlich
            HelperMethods.Debug("Released all previously allocated resources!", printDebugInfo);
            this.dllName = dllName.Split('\\').Last();
        }

        public void Invoke(string functionName, string param)
        {
            if (string.IsNullOrEmpty(dllName))
            {
                return;
            }
            string asdf = "0x" + (target.Process.MainModule.BaseAddress.ToInt64() + LibraryHandle.ToInt64()).ToString("x");
            IntPtr[] intPtrs = new IntPtr[256];
            WinAPI.EnumProcessModules(target.Handle, intPtrs, 256, out uint cbNeeded);
            byte[] b = new byte[32];
            uint c = WinAPI.GetModuleBaseNameA(target.Handle, LibraryHandle, b, (uint)b.Length);
            ProcessModuleCollection modules = target.Process.Modules;
            ProcessModule module = null;
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i].ModuleName.Equals(dllName))
                {
                    module = modules[i];
                }
            }
            IntPtr moduleAddress = module.BaseAddress;
            uint errorCode = WinAPI.GetLastError();
            IntPtr functionAddress = WinAPI.GetProcAddress(moduleAddress, functionName);
            if (functionAddress == IntPtr.Zero)
            {
                errorCode = WinAPI.GetLastError();
                throw new Win32Exception((int)errorCode, "Encountered error " + errorCode.ToString() + " (0x" + errorCode.ToString("x") + ")");
            }
            uint size = (uint)((param.Length + 1) * Marshal.SizeOf(typeof(char)));
            IntPtr allocMemAddress = WinAPI.VirtualAllocEx(target.Handle, IntPtr.Zero, size, (uint)Permissions.MemoryPermission.MEM_COMMIT | (uint)Permissions.MemoryPermission.MEM_RESERVE, (uint)Permissions.MemoryPermission.PAGE_READWRITE);
            int bytesWritten = 0;
            // writing the name of the dll there
            byte[] buffer = new byte[size];
            byte[] bytes = Encoding.ASCII.GetBytes(param);
            Array.Copy(bytes, 0, buffer, 0, bytes.Length);
            buffer[buffer.Length - 1] = 0;
            bool success = WinAPI.WriteProcessMemory((uint)target.Handle, target.Is32BitProcess ? allocMemAddress.ToInt32() : allocMemAddress.ToInt64(), buffer, size, ref bytesWritten);
            IntPtr threadHandle = WinAPI.CreateRemoteThread(target.Handle, IntPtr.Zero, 0, functionAddress, allocMemAddress, 0, out _);
            uint waitExitCode = WinAPI.WaitForSingleObject(threadHandle, 10 * 1000);
            success = WinAPI.GetExitCodeThread(threadHandle, out _);
            success = WinAPI.CloseHandle(threadHandle);
            success = WinAPI.VirtualFreeEx(target.Handle, allocMemAddress, 0, 0x8000);
        }

        public void Free()
        {
            if (string.IsNullOrEmpty(dllName))
            {
                return;
            }
            ProcessModuleCollection modules = target.Process.Modules;
            ProcessModule module = null;
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i].ModuleName.Equals(dllName))
                {
                    module = modules[i];
                    break;
                }
            }
            if (module == null)
            {
                throw new ModuleNotFoundException();
            }
            IntPtr kernel32Handle = WinAPI.GetModuleHandle("kernel32.dll");
            IntPtr freeLibraryAddress = WinAPI.GetProcAddress(kernel32Handle, "FreeLibrary");
            IntPtr threadHandle = WinAPI.CreateRemoteThread(target.Handle, IntPtr.Zero, 0, freeLibraryAddress, LibraryHandle, 0, out _);
            uint waitExitCode = WinAPI.WaitForSingleObject(threadHandle, 10 * 1000);
            bool success = WinAPI.GetExitCodeThread(threadHandle, out _);
            success = WinAPI.CloseHandle(threadHandle);
        }
    }
}
