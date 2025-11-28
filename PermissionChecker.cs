using System;
using System.Collections.Generic;

namespace NEDA.Core.Security
{
    // Basit bir izin kontrol sınıfı
    public static class PermissionChecker
    {
        // Örnek kullanıcı izinleri
        private static readonly Dictionary<string, List<string>> _userPermissions = new()
        {
            { "Admin", new List<string> { "Read", "Write", "Execute" } },
            { "User", new List<string> { "Read" } },
            { "Guest", new List<string>() }
        };

        /// <summary>
        /// Kullanıcının belirli bir izne sahip olup olmadığını kontrol eder.
        /// </summary>
        /// <param name="role">Kullanıcı rolü (Admin, User, Guest vb.)</param>
        /// <param name="permission">Kontrol edilecek izin (Read, Write, Execute vb.)</param>
        /// <returns>İzin varsa true, yoksa false döner</returns>
        public static bool HasPermission(string role, string permission)
        {
            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(permission))
                return false;

            if (_userPermissions.TryGetValue(role, out var permissions))
            {
                return permissions.Contains(permission);
            }

            return false;
        }

        /// <summary>
        /// Kullanıcı rolüne yeni bir izin ekler (opsiyonel)
        /// </summary>
        public static void AddPermission(string role, string permission)
        {
            if (!_userPermissions.ContainsKey(role))
                _userPermissions[role] = new List<string>();

            if (!_userPermissions[role].Contains(permission))
                _userPermissions[role].Add(permission);
        }
    }
}
