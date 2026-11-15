using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DriverPilot;

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
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriverPilot");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string MachineRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriverPilot");

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
