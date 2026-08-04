using System;
using Npgsql;
using NSPGatekeeper.Controller.Infrastructure.Logging;

namespace NSPGatekeeper.Controller.Infrastructure.Database
{
    public sealed class DatabaseBootstrapper
    {
        private readonly string _applicationConnectionString;
        private readonly string _adminConnectionString;
        private readonly FileLogger _logger;

        public DatabaseBootstrapper(string applicationConnectionString, string adminConnectionString, FileLogger logger)
        {
            if (string.IsNullOrWhiteSpace(applicationConnectionString))
                throw new ArgumentException("PostgreSqlConnectionString is required.");

            _applicationConnectionString = NormalizeConnectionStringValue(applicationConnectionString, required: true);
            _adminConnectionString = NormalizeConnectionStringValue(adminConnectionString, required: false);
            _logger = logger;
        }

        public void EnsureDatabase()
        {
            var application = ParseApplicationConnection();

            Exception targetError;
            if (CanOpen(_applicationConnectionString, out targetError))
            {
                LogInfo("Local PostgreSQL database is available", "database=" + application.Database);
                return;
            }

            LogWarn(
                "Local PostgreSQL database is not ready; bootstrap will be attempted",
                "database=" + application.Database + " error=" + SafeError(targetError));

            var adminConnectionString = BuildAdminConnectionString(application);
            try
            {
                using (var admin = new NpgsqlConnection(adminConnectionString))
                {
                    admin.Open();
                    EnsureApplicationRole(admin, application.Username, application.Password);
                    EnsureDatabaseRecord(admin, application.Database, application.Username);
                    EnsureConnectPrivilege(admin, application.Database, application.Username);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Local PostgreSQL bootstrap failed. Configure PostgreSqlAdminConnectionString with a PostgreSQL " +
                    "administrator account that can CREATE ROLE and CREATE DATABASE, or create the role/database during deployment. " +
                    "Details: " + SafeError(ex), ex);
            }

            Exception verifyError;
            if (!CanOpen(_applicationConnectionString, out verifyError))
            {
                throw new InvalidOperationException(
                    "Local PostgreSQL database exists, but the Controller cannot connect with the configured application credentials. " +
                    "Check PostgreSqlConnectionString username/password and pg_hba.conf. Details: " + SafeError(verifyError),
                    verifyError);
            }

            LogInfo("Local PostgreSQL bootstrap completed", "database=" + application.Database + " owner=" + application.Username);
        }

        private static string NormalizeConnectionStringValue(string value, bool required)
        {
            var text = (value ?? string.Empty).Trim();

            if (text.Length == 0 || text == "\"\"" || text == "''")
            {
                if (required)
                    throw new ArgumentException("PostgreSqlConnectionString is required.");
                return string.Empty;
            }

            if (text.Length >= 2 &&
                ((text[0] == '\"' && text[text.Length - 1] == '\"') ||
                 (text[0] == '\'' && text[text.Length - 1] == '\'')))
            {
                text = text.Substring(1, text.Length - 2).Trim();
            }

            return text;
        }

        private NpgsqlConnectionStringBuilder ParseApplicationConnection()
        {
            NpgsqlConnectionStringBuilder builder;
            try
            {
                builder = new NpgsqlConnectionStringBuilder(_applicationConnectionString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("PostgreSqlConnectionString is invalid: " + SafeError(ex), ex);
            }

            if (string.IsNullOrWhiteSpace(builder.Host))
                throw new InvalidOperationException("PostgreSqlConnectionString Host is required.");
            if (string.IsNullOrWhiteSpace(builder.Database))
                throw new InvalidOperationException("PostgreSqlConnectionString Database is required.");
            if (string.IsNullOrWhiteSpace(builder.Username))
                throw new InvalidOperationException("PostgreSqlConnectionString Username is required.");

            return builder;
        }

        private string BuildAdminConnectionString(NpgsqlConnectionStringBuilder application)
        {
            NpgsqlConnectionStringBuilder admin = null;

            if (!string.IsNullOrWhiteSpace(_adminConnectionString))
            {
                try
                {
                    admin = new NpgsqlConnectionStringBuilder(_adminConnectionString);
                    if (string.IsNullOrWhiteSpace(admin.Host) || string.IsNullOrWhiteSpace(admin.Username))
                    {
                        LogWarn(
                            "PostgreSqlAdminConnectionString is incomplete; application credentials will be used as fallback",
                            "required=Host,Username");
                        admin = null;
                    }
                }
                catch (Exception ex)
                {
                    LogWarn(
                        "PostgreSqlAdminConnectionString is invalid and will be ignored",
                        "error=" + SafeError(ex));
                    admin = null;
                }
            }

            if (admin == null)
            {
                admin = new NpgsqlConnectionStringBuilder(_applicationConnectionString);
                LogInfo(
                    "Using application PostgreSQL credentials for bootstrap fallback",
                    "host=" + admin.Host + " username=" + admin.Username);
            }

            admin.Database = "postgres";
            admin.Pooling = false;
            return admin.ConnectionString;
        }

        private void EnsureApplicationRole(NpgsqlConnection admin, string roleName, string rolePassword)
        {
            using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname=@name", admin))
            {
                exists.Parameters.AddWithValue("name", roleName);
                if (exists.ExecuteScalar() != null)
                {
                    LogInfo("PostgreSQL application role already exists", "role=" + roleName);
                    return;
                }
            }

            if (string.IsNullOrEmpty(rolePassword))
            {
                throw new InvalidOperationException(
                    "The application role does not exist and PostgreSqlConnectionString does not contain a password. " +
                    "Set the application password before first-start bootstrap.");
            }

            var sql = "CREATE ROLE " + QuoteIdentifier(roleName) + " LOGIN PASSWORD " + QuoteLiteral(rolePassword);
            try
            {
                using (var create = new NpgsqlCommand(sql, admin)) create.ExecuteNonQuery();
                LogInfo("PostgreSQL application role created", "role=" + roleName);
            }
            catch (PostgresException ex) when (ex.SqlState == "42710")
            {
                LogInfo("PostgreSQL application role was created concurrently", "role=" + roleName);
            }
        }

        private void EnsureDatabaseRecord(NpgsqlConnection admin, string databaseName, string ownerRole)
        {
            using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname=@name", admin))
            {
                exists.Parameters.AddWithValue("name", databaseName);
                if (exists.ExecuteScalar() != null)
                {
                    LogInfo("PostgreSQL database already exists", "database=" + databaseName);
                    return;
                }
            }

            // CREATE DATABASE cannot execute inside a transaction block.
            var sql = "CREATE DATABASE " + QuoteIdentifier(databaseName) +
                      " OWNER " + QuoteIdentifier(ownerRole) +
                      " ENCODING 'UTF8' TEMPLATE template0";
            try
            {
                using (var create = new NpgsqlCommand(sql, admin)) create.ExecuteNonQuery();
                LogInfo("PostgreSQL database created", "database=" + databaseName + " owner=" + ownerRole);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P04")
            {
                LogInfo("PostgreSQL database was created concurrently", "database=" + databaseName);
            }
        }

        private void EnsureConnectPrivilege(NpgsqlConnection admin, string databaseName, string roleName)
        {
            var sql = "GRANT CONNECT ON DATABASE " + QuoteIdentifier(databaseName) + " TO " + QuoteIdentifier(roleName);
            using (var grant = new NpgsqlCommand(sql, admin)) grant.ExecuteNonQuery();
        }

        private static bool CanOpen(string connectionString, out Exception error)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private static string QuoteIdentifier(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string QuoteLiteral(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string SafeError(Exception ex)
        {
            if (ex == null) return "unknown_error";
            var postgres = ex as PostgresException;
            if (postgres != null)
                return "SQLSTATE=" + postgres.SqlState + " " + postgres.MessageText;
            return ex.Message;
        }

        private void LogInfo(string message, string details)
        {
            if (_logger != null) _logger.Info("db-bootstrap", message, details);
        }

        private void LogWarn(string message, string details)
        {
            if (_logger != null) _logger.Warn("db-bootstrap", message, details);
        }
    }
}
