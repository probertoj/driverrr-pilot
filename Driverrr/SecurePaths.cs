using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Driverrr;

/// <summary>
/// Dos zonas de disco con distinta confianza:
///  - Usuario (%LocalAppData%): caché y registro de la app sin elevar. Cualquier programa del usuario puede
///    modificarla, así que su contenido NUNCA se da por bueno cuando la app corre como administrador.
///  - Máquina (%ProgramData%): solo la escribe la app elevada, con permisos que impiden a programas sin elevar
///    plantar ficheros o enlaces en mitad de una instalación.
/// </summary>
public static class SecurePaths
{
    public static string UserRoot
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Driverrr");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Nombre que tuvo la app en sus primeras versiones; solo se usa para no perder sus datos.</summary>
    internal const string LegacyName = "DriverPilot";

    static string NewMachineRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Driverrr");

    static string LegacyMachineRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyName);

    /// <summary>
    /// Zona de máquina. Si solo existe la carpeta del nombre antiguo (con las copias de seguridad de drivers),
    /// la app elevada la renombra; sin elevar se lee de la antigua para que «Restaurar» siga mostrando las copias.
    /// </summary>
    public static string MachineRoot
    {
        get
        {
            if (Directory.Exists(NewMachineRoot) || !Directory.Exists(LegacyMachineRoot)) return NewMachineRoot;
            if (!SafetyService.IsAdmin()) return LegacyMachineRoot;
            try
            {
                // Solo se adopta una carpeta antigua de fiar: propiedad de Administradores y que no sea un enlace.
                AssertTrusted(LegacyMachineRoot);
                Directory.Move(LegacyMachineRoot, NewMachineRoot);
                return NewMachineRoot;
            }
            catch
            {
                return NewMachineRoot;   // no se pudo adoptar: se empieza de cero en la carpeta nueva
            }
        }
    }

    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>Devuelve una subcarpeta protegida de la zona de máquina, creándola si hace falta. Requiere administrador.</summary>
    public static string MachineDir(params string[] parts)
    {
        HardenRoot(MachineRoot);
        var path = MachineRoot;
        foreach (var part in parts)
        {
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part is "." or "..")
                throw new SecurityException("Nombre de carpeta no válido.");
            path = Path.Combine(path, part);
            if (Directory.Exists(path)) AssertTrusted(path);
            else Directory.CreateDirectory(path);   // hereda los permisos protegidos de la raíz
        }
        return path;
    }

    /// <summary>Borra carpetas de trabajo que hayan quedado de operaciones anteriores (más de una hora).</summary>
    public static void PurgeWork()
    {
        try
        {
            var work = Path.Combine(MachineRoot, "Work");
            if (!Directory.Exists(work) || IsReparsePoint(work)) return;
            foreach (var dir in Directory.EnumerateDirectories(work))
            {
                try
                {
                    if (!IsReparsePoint(dir) && DateTime.Now - Directory.GetCreationTime(dir) > TimeSpan.FromHours(1))
                        Directory.Delete(dir, true);
                }
                catch { }   // en uso: se borrará la próxima vez
            }
        }
        catch { }
    }

    /// <summary>Carpeta de trabajo nueva y vacía para una instalación.</summary>
    public static string NewWorkDir() => MachineDir("Work", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Deja la raíz en manos de Administradores: propietario Administradores, sin herencia,
    /// control total para SYSTEM y Administradores y solo lectura para el resto.
    /// </summary>
    internal static void HardenRoot(string root)
    {
        if (Directory.Exists(root))
        {
            if (IsReparsePoint(root)) throw new SecurityException($"«{root}» es un enlace; se rechaza por seguridad.");
        }
        else
        {
            Directory.CreateDirectory(root);
        }

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var sec = new DirectorySecurity();
        sec.SetOwner(admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(sec);
    }

    /// <summary>Una carpeta ya existente solo vale si no es un enlace y pertenece a Administradores o SYSTEM.</summary>
    internal static void AssertTrusted(string dir)
    {
        if (IsReparsePoint(dir))
            throw new SecurityException($"«{dir}» es un enlace; se rechaza por seguridad.");

        var owner = new DirectoryInfo(dir).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null ||
            !(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
            throw new SecurityException($"«{dir}» no pertenece a Administradores; pudo crearla otro programa. Bórrala y reintenta.");
    }

    internal static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
