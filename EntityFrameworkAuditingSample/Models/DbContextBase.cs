﻿using Microsoft.AspNet.Identity.EntityFramework;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace EntityFrameworkAuditingSample.Models
{
    /// <summary>
    /// DbContextBase is a base class that can easily add auditing on top of your existing entity framework solution
    /// This class needs the 
    /// - OwinContextHelper class from http://go.beeming.net/2k1TgEQ    
    /// - Audit class from http://go.beeming.net/2k1Zbd4
    /// </summary>
    /// <typeparam name="TUser"></typeparam>
    public abstract class DbContextBase<TUser> : IdentityDbContext<TUser> where TUser : IdentityUser
    {
        public DbSet<Audit> Audits { get; set; }

        public DbContextBase()
            : base("DefaultConnection", throwIfV1Schema: false)
        {
        }

        public override int SaveChanges()
        {
            return PrivateSaveChangesAsync(CancellationToken.None).Result;
        }

        public override Task<int> SaveChangesAsync()
        {
            return PrivateSaveChangesAsync(CancellationToken.None);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            return PrivateSaveChangesAsync(cancellationToken);
        }

        private async Task<int> PrivateSaveChangesAsync(CancellationToken cancellationToken)
        {
            // If you want to have audits in transaction with records you must handle
            // transactions manually
            using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
            {
                ObjectContext context = ((IObjectContextAdapter)this).ObjectContext;
                await context.SaveChangesAsync(SaveOptions.DetectChangesBeforeSave, cancellationToken).ConfigureAwait(false);

                // Now you must call your audit code but instead of adding audits to context
                // you must add them to list. 

                var audits = new List<Audit>();

                var currentUser = OwinContextHelper.CurrentApplicationUser;
                foreach (var entry in ChangeTracker.Entries().Where(o => o.State != EntityState.Unchanged && o.State != EntityState.Detached).ToList())
                {
                    var changeType = entry.State.ToString();
                    Type entityType = GetEntityType(entry);

                    string tableName = GetTableName(context, entityType);

                    string identityJson = GetIdentityJson(entry, entityType);

                    var audit = new Audit
                    {
                        Id = Guid.NewGuid(),
                        AuditUserId = currentUser?.Id != null ? currentUser.Id : null,
                        ChangeType = changeType,
                        ObjectType = entityType.ToString(),
                        FromJson = (entry.State == EntityState.Added ? "{  }" : GetAsJson(entry.OriginalValues)),
                        ToJson = (entry.State == EntityState.Deleted ? "{  }" : GetAsJson(entry.CurrentValues)),
                        TableName = tableName,
                        IdentityJson = identityJson,
                        DateCreated = DateTime.UtcNow,
                    };
#if DEBUG
                    if (audit.FromJson == audit.ToJson)
                    {
                        throw new Exception($"Something went wrong because this {audit.ChangeType} Audit shows no changes!");
                    }
#endif
                    audits.Add(audit);
                }

                // This is the reason why you must not add changes to context. You must accept
                // old changes prior to adding your new audit records otherwise EF will perform
                // changes again. If you add your entities to context and call accept before 
                // saving them your changes will be lost
                context.AcceptAllChanges();

                // Now add all audits from list to context
                Audits.AddRange(audits);

                int result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // Complete the transaction
                scope.Complete();

                return result;
            }
        }

        private string GetIdentityJson(DbEntityEntry entry, Type entityType)
        {
            string identityJson = string.Empty;
            foreach (var field in entityType.GetProperties().Where(o => o.CustomAttributes.FirstOrDefault(oo => oo.AttributeType == typeof(System.ComponentModel.DataAnnotations.KeyAttribute)) != null))
            {
                if (identityJson.Length > 0)
                {
                    identityJson += ", ";
                }

                identityJson += $@"""{field.Name}"":{GetFieldValue(field.Name, (entry.State == EntityState.Deleted ? entry.OriginalValues : entry.CurrentValues))}";
            }
            identityJson = $"{{ {identityJson} }}";
            return identityJson;
        }

        private object GetFieldValue(string name, DbPropertyValues values)
        {
            var val = values[name];
            return val == null ? "null" : (IsNumber(val) ? val.ToString() : $@"""{val}""");
        }

        private static Type GetEntityType(DbEntityEntry entry)
        {
            Type entityType = entry.Entity.GetType();

            if (entityType.BaseType != null && entityType.Namespace == "System.Data.Entity.DynamicProxies")
                entityType = entityType.BaseType;
            return entityType;
        }

        private static string GetTableName(ObjectContext context, Type entityType)
        {
            string entityTypeName = entityType.Name;

            EntityContainer container = context.MetadataWorkspace.GetEntityContainer(context.DefaultContainerName, DataSpace.CSpace);
            string tableName = (from meta in container.BaseEntitySets
                                where meta.ElementType.Name == entityTypeName
                                select meta.Name).First();
            return tableName;
        }

        private static string GetAsJson(DbPropertyValues values)
        {
            string json = string.Empty;
            if (values != null)
            {
                foreach (var propertyName in values.PropertyNames)
                {
                    if (json.Length > 0)
                    {
                        json += ", ";
                    }
                    var val = values[propertyName];
                    json += $@"""{propertyName}"":{(val == null ? "null" : (IsNumber(val) ? val.ToString() : $@"""{val}"""))}";
                }
            }
            return $"{{ {json} }}";
        }

        public static bool IsNumber(object value)
        {
            return value is sbyte
                    || value is byte
                    || value is short
                    || value is ushort
                    || value is int
                    || value is uint
                    || value is long
                    || value is ulong
                    || value is float
                    || value is double
                    || value is decimal;
        }
    }
}
