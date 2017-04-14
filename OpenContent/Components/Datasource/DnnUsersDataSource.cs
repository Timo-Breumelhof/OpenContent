﻿using DotNetNuke.Common;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Profile;
using DotNetNuke.Entities.Users;
using DotNetNuke.Entities.Users.Membership;
using DotNetNuke.Security.Membership;
using DotNetNuke.Security.Roles;
using DotNetNuke.Services.Mail;
using Newtonsoft.Json.Linq;
using Satrabel.OpenContent.Components.Alpaca;
using Satrabel.OpenContent.Components.Lucene;
using System;
using System.Collections.Generic;
using System.Linq;
using Satrabel.OpenContent.Components.Lucene.Index;
using Satrabel.OpenContent.Components.Lucene.Config;
using Satrabel.OpenContent.Components.Logging;
using Satrabel.OpenContent.Components.Datasource.Search;
using Satrabel.OpenContent.Components.Localization;

namespace Satrabel.OpenContent.Components.Datasource
{
    public class DnnUsersDataSource : DefaultDataSource, IDataActions, IDataIndex
    {
        private const string REGISTERED_USERS = "Registered Users";
        private const string LUCENE_SCOPE = "DnnUsers";

        public override string Name => "Satrabel.DnnUsers";

        public override IDataItem Get(DataSourceContext context, string id)
        {
            //return GetAll(context, null).Items.SingleOrDefault(i => i.Id == id);
            var user = UserController.GetUserById(context.PortalId, int.Parse(id));
            if (user.IsInRole("Administrators"))
            {
                throw new Exception("Not autorized to get Administrator users");
            }
            return ToData(user);
        }
        private IDataItem ToData(UserInfo user)
        {
            var item = new DefaultDataItem()
            {
                Id = user.UserID.ToString(),
                Title = user.DisplayName,
                Data = JObject.FromObject(new
                {
                    user.UserID,
                    user.DisplayName,
                    user.Username,
                    user.FirstName,
                    user.LastName,
                    user.Email,
                    user.Membership.Approved,
                    user.Membership.LockedOut,
                    user.IsDeleted
                }),
                CreatedByUserId = user.CreatedByUserID,
                LastModifiedByUserId = user.LastModifiedByUserID,
                LastModifiedOnDate = user.LastModifiedOnDate,
                CreatedOnDate = user.CreatedOnDate,
                Item = user
            };
            item.Data["Profile"] = new JObject();
            foreach (ProfilePropertyDefinition def in user.Profile.ProfileProperties)
            {
                //ProfilePropertyAccess.GetRichValue(def, )
                if (def.PropertyName.ToLower() == "photo")
                {
                    item.Data["Profile"]["PhotoURL"] = user.Profile.PhotoURL;
                    item.Data["Profile"][def.PropertyName] = def.PropertyValue;
                }
                else
                {
                    item.Data["Profile"][def.PropertyName] = def.PropertyValue;
                }
            }
            var roles = new JArray();
            item.Data["Roles"] = roles;
            foreach (var role in user.Roles)
            {
                if (role != REGISTERED_USERS)
                {
                    roles.Add(role);
                }
            }
            return item;
        }

        public override IDataItems GetAll(DataSourceContext context, Select selectQuery)
        {
            if (context.Index && selectQuery != null)
            {
                SelectQueryDefinition def = BuildQuery(context, selectQuery);
                SearchResults docs = LuceneController.Instance.Search(LUCENE_SCOPE, def.Filter, def.Query, def.Sort, def.PageSize, def.PageIndex);
                int total = docs.TotalResults;
                var dataList = new List<IDataItem>();
                foreach (string item in docs.ids)
                {
                    var user = UserController.GetUserById(context.PortalId, int.Parse(item));
                    if (user != null)
                    {
                        dataList.Add(ToData(user));
                    }
                    else
                    {
                        Log.Logger.DebugFormat("DnnUsersDataSource.GetAll() ContentItem not found [{0}]", item);
                    }
                }
                return new DefaultDataItems()
                {
                    Items = dataList,
                    Total = total,
                    DebugInfo = def.Filter + " - " + def.Query + " - " + def.Sort
                };
            }
            else
            {
                int pageIndex = 0;
                int pageSize = 1000;
                int total = 0;
                IEnumerable<UserInfo> users;
                if (selectQuery != null)
                {
                    pageIndex = selectQuery.PageIndex;
                    pageSize = selectQuery.PageSize;
                    var ruleDisplayName = selectQuery.Query.FilterRules.FirstOrDefault(f => f.Field == "DisplayName");
                    var ruleRoles = selectQuery.Query.FilterRules.FirstOrDefault(f => f.Field == "Roles");
                    if (ruleDisplayName != null)
                    {
                        string displayName = ruleDisplayName.Value.AsString + "%";
                        users = UserController.GetUsersByDisplayName(context.PortalId, displayName, pageIndex, pageSize, ref total, true, false).Cast<UserInfo>();
                    }
                    else
                    {
                        users = UserController.GetUsers(context.PortalId, pageIndex, pageSize, ref total, true, false).Cast<UserInfo>();
                        total = users.Count();
                    }
                    if (ruleRoles != null)
                    {
                        var roleNames = ruleRoles.MultiValue.Select(r => r.AsString).ToList();
                        users = users.Where(u => u.Roles.Intersect(roleNames).Any());
                    }
                }
                else
                {
                    users = UserController.GetUsers(context.PortalId, pageIndex, pageSize, ref total, true, false).Cast<UserInfo>();
                }
                users = users.Where(u => !u.IsInRole("Administrators"));
                //users = users.Skip(pageIndex * pageSize).Take(pageSize);
                var dataList = new List<IDataItem>();
                foreach (var user in users)
                {
                    dataList.Add(ToData(user));
                }
                return new DefaultDataItems()
                {
                    Items = dataList,
                    Total = total,
                    //DebugInfo = 
                };
            }
        }
        private static SelectQueryDefinition BuildQuery(DataSourceContext context, Select selectQuery)
        {
            SelectQueryDefinition def = new SelectQueryDefinition();
            def.Build(selectQuery);
            if (LogContext.IsLogActive)
            {
                var logKey = "Lucene query";
                LogContext.Log(context.ActiveModuleId, logKey, "Filter", def.Filter.ToString());
                LogContext.Log(context.ActiveModuleId, logKey, "Query", def.Query.ToString());
                LogContext.Log(context.ActiveModuleId, logKey, "Sort", def.Sort.ToString());
                LogContext.Log(context.ActiveModuleId, logKey, "PageIndex", def.PageIndex);
                LogContext.Log(context.ActiveModuleId, logKey, "PageSize", def.PageSize);
            }

            return def;
        }

        public override JObject GetAlpaca(DataSourceContext context, bool schema, bool options, bool view)
        {
            var fb = new FormBuilder(new FolderUri(context.TemplateFolder));
            var alpaca = fb.BuildForm("", context.CurrentCultureCode);
            return alpaca;
        }
        public override void Add(DataSourceContext context, Newtonsoft.Json.Linq.JToken data)
        {
            var schema = GetAlpaca(context, true, false, false)["schema"] as JObject;
            var user = new UserInfo
            {
                AffiliateID = Null.NullInteger,
                PortalID = context.PortalId,
                IsDeleted = false,
                IsSuperUser = false,
                Profile = new UserProfile()
            };

            //user.LastIPAddress = Request.UserHostAddress
            var ps = PortalSettings.Current;
            user.Profile.InitialiseProfile(ps.PortalId, true);
            user.Profile.PreferredLocale = ps.DefaultLanguage;
            user.Profile.PreferredTimeZone = ps.TimeZone;
            if (HasProperty(schema, "", "Username"))
            {
                user.Username = data["Username"]?.ToString() ?? "";
            }
            if (HasProperty(schema, "", "Password"))
            {
                user.Membership.Password = data["Password"]?.ToString() ?? "";
            }
            FillUser(data, schema, user);
            UpdateDisplayName(context, user);
            if (string.IsNullOrEmpty(user.DisplayName))
            {
                user.DisplayName = user.FirstName + " " + user.LastName;
            }
            user.Membership.Approved = true;  //chkAuthorize.Checked;
            var newUser = user;
            var createStatus = UserController.CreateUser(ref newUser);
            bool notify = true;
            if (createStatus == UserCreateStatus.Success)
            {
                string strMessage = "";
                if (notify)
                {
                    //Send Notification to User
                    if (ps.UserRegistration == (int)Globals.PortalRegistrationType.VerifiedRegistration)
                    {
                        strMessage += Mail.SendMail(newUser, MessageType.UserRegistrationVerified, ps);
                    }
                    else
                    {
                        strMessage += Mail.SendMail(newUser, MessageType.UserRegistrationPublic, ps);
                    }
                }
                if (string.IsNullOrEmpty(strMessage))
                {
                    throw new Exception("Error sending notification email: " + strMessage);

                }
                FillProfile(data, schema, user);
                UpdateRoles(context, data, schema, user);
            }
            else
            {
                Log.Logger.Error($"Creation of user failed with createStatus: {createStatus}");
                throw new DataNotValidException(Localizer.Instance.GetString(createStatus.ToString()) + " (1)");
            }
            var indexConfig = OpenContentUtils.GetIndexConfig(new FolderUri(context.TemplateFolder), context.Collection);
            if (context.Index)
            {
                LuceneController.Instance.Add(new IndexableItemUser()
                {
                    Data = data,
                    User = user
                }, indexConfig);
                LuceneController.Instance.Store.Commit();
            }
        }

        public override void Update(DataSourceContext context, IDataItem item, Newtonsoft.Json.Linq.JToken data)
        {
            var schema = GetAlpaca(context, true, false, false)["schema"] as JObject;
            var user = (UserInfo)item.Item;
            FillUser(data, schema, user);
            UserController.UpdateUser(context.PortalId, user);
            if (HasProperty(schema, "", "Approved"))
            {
                bool approved = (bool)(data["Approved"] as JValue).Value;
                if (approved != user.Membership.Approved)
                {
                    if (approved)
                    {
                        user.Membership.Approved = true;
                        UserController.UpdateUser(context.PortalId, user);
                        //Update User Roles if needed
                        if (!user.IsSuperUser && user.IsInRole("Unverified Users") && PortalSettings.Current.UserRegistration == (int)Globals.PortalRegistrationType.VerifiedRegistration)
                        {
                            UserController.ApproveUser(user);
                        }
                    }
                    else
                    {
                        user.Membership.Approved = false;
                    }
                }
            }
            UserController.UpdateUser(context.PortalId, user);
            if (HasProperty(schema, "", "Password") && data["Password"] != null)
            {
                string password = data["Password"].ToString();
                ChangePassword(user, password);
            }
            if (HasProperty(schema, "", "Username") && !string.IsNullOrEmpty(data["Username"]?.ToString()))
            {
                var userName = data["Username"].ToString();
                if (userName != user.Username)
                {
                    UpdateDisplayName(context, user);
                    try
                    {
                        //Update DisplayName to conform to Format
                        UserController.ChangeUsername(user.UserID, userName);
                    }
                    catch (Exception exc)
                    {
                        Log.Logger.Error($"Update of user {user.Username} failed with 'Username not valid' error");
                        throw new DataNotValidException(Localizer.Instance.GetString("Username not valid") + " (2)", exc);
                    }
                }
            }
            FillProfile(data, schema, user);
            UpdateRoles(context, data, schema, user);

            var indexConfig = OpenContentUtils.GetIndexConfig(new FolderUri(context.TemplateFolder), context.Collection);
            if (context.Index)
            {
                LuceneController.Instance.Update(new IndexableItemUser()
                {
                    Data = data,
                    User = user
                }, indexConfig);
                LuceneController.Instance.Store.Commit();
            }
        }

        public override void Delete(DataSourceContext context, IDataItem item)
        {
            var user = (UserInfo)item.Item;
            UserController.DeleteUser(ref user, true, false);

        }

        public List<IDataAction> GetActions(DataSourceContext context, IDataItem item)
        {
            var actions = new List<IDataAction>();
            if (item == null) return actions;

            var user = (UserInfo)item.Item;
            if (user.Membership.LockedOut)
            {
                actions.Add(new DefaultDataAction()
                {
                    Name = "unlock",
                    AfterExecute = "disable"
                });
            }
            return actions;
        }

        public override JToken Action(DataSourceContext context, string action, IDataItem item, JToken data)
        {
            if (action == "unlock")
            {
                var user = (UserInfo)item.Item;
                UserController.UnLockUser(user);
            }
            return null;
        }

        public void Reindex(DataSourceContext context)
        {
            string scope = LUCENE_SCOPE;
            var indexConfig = OpenContentUtils.GetIndexConfig(new FolderUri(context.TemplateFolder), context.Collection); //todo index is being build from schema & options. But they should be provided by the provider, not directly from the files
            LuceneController.Instance.ReIndexModuleData(UserController.GetUsers(true, false, context.PortalId).Cast<UserInfo>().
                Where(u => !u.IsInRole("Administrators")).Select(u => new IndexableItemUser()
                {
                    Data = ToData(u).Data,
                    User = u
                }), indexConfig, scope);
        }

        #region private methods

        private static void UpdateRoles(DataSourceContext context, JToken data, JObject schema, UserInfo user)
        {
            if (HasProperty(schema, "", "Roles"))
            {
                List<string> rolesToRemove = new List<string>(user.Roles);
                var roles = data["Roles"] as JArray;
                foreach (var role in roles)
                {
                    string roleName = role.ToString();
                    rolesToRemove.Remove(roleName);
                    if (!user.Roles.Contains(roleName))
                    {
                        var roleInfo = RoleController.Instance.GetRoleByName(context.PortalId, roleName);
                        RoleController.AddUserRole(user, roleInfo, PortalSettings.Current, RoleStatus.Approved, Null.NullDate, Null.NullDate, false, false);
                    }
                }
                foreach (var roleName in rolesToRemove)
                {
                    if (roleName != REGISTERED_USERS)
                    {
                        var roleInfo = RoleController.Instance.GetRoleByName(context.PortalId, roleName);
                        RoleController.DeleteUserRole(user, roleInfo, PortalSettings.Current, false);
                    }
                }
            }
        }


        private static void ChangePassword(UserInfo user, string password)
        {
            // Check New Password is Valid
            if (!UserController.ValidatePassword(password))
            {
                Log.Logger.Error($"Changing password of user {user.Username} failed with PasswordInvalid error");
                throw new DataNotValidException(Localizer.Instance.GetString("PasswordInvalid"));
            }
            // Check New Password is not same as username or banned
            var settings = new MembershipPasswordSettings(user.PortalID);
            if (settings.EnableBannedList)
            {
                var m = new MembershipPasswordController();
                if (m.FoundBannedPassword(password) || user.Username == password)
                {
                    Log.Logger.Error($"Changing password of user {user.Username} failed with BannedPasswordUsed error");
                    throw new DataNotValidException(Localizer.Instance.GetString("BannedPasswordUsed"));
                }
            }
            UserController.ResetAndChangePassword(user, password);
        }

        private static void FillProfile(JToken data, JObject schema, UserInfo user)
        {
            if (HasProperty(schema, "", "Profile"))
            {
                ProfileController pc = new ProfileController();
                var profile = data["Profile"] as JObject;
                var profileSchema = schema["properties"]["Profile"]["properties"] as JObject;
                foreach (var prop in profileSchema.Properties())
                {
                    if (user.Profile.GetProperty(prop.Name) != null)
                    {
                        if (profile[prop.Name] != null)
                        {
                            if (prop.Name.ToLower() == "photo")
                            {
                                user.Profile.SetProfileProperty(prop.Name, profile[prop.Name].ToString());
                            }
                            else
                            {
                                user.Profile.SetProfileProperty(prop.Name, profile[prop.Name].ToString());
                            }
                        }
                        else
                        {
                            user.Profile.SetProfileProperty(prop.Name, "");
                        }
                    }
                }
                ProfileController.UpdateUserProfile(user);
            }
        }

        private static void FillUser(JToken data, JObject schema, UserInfo user)
        {
            if (HasProperty(schema, "", "DisplayName"))
            {
                user.DisplayName = data["DisplayName"]?.ToString() ?? "";
            }
            if (HasProperty(schema, "", "FirstName"))
            {
                user.FirstName = data["FirstName"]?.ToString() ?? "";
            }
            if (HasProperty(schema, "", "LastName"))
            {
                user.LastName = data["LastName"]?.ToString() ?? "";
            }
            if (HasProperty(schema, "", "Email"))
            {
                user.Email = data["Email"]?.ToString() ?? "";
            }
        }

        private static void UpdateDisplayName(DataSourceContext context, UserInfo user)
        {
            //Update DisplayName to conform to Format
            object setting = UserModuleBase.GetSetting(context.PortalId, "Security_DisplayNameFormat");
            if ((setting != null) && (!string.IsNullOrEmpty(Convert.ToString(setting))))
            {
                user.UpdateDisplayName(Convert.ToString(setting));
            }
        }

        private static bool HasProperty(JObject schema, string subobject, string property)
        {
            if (!string.IsNullOrEmpty(subobject))
            {
                schema = schema[subobject] as JObject;
            }
            if (schema == null || !(schema["properties"] is JObject)) return false;

            return ((JObject)schema["properties"]).Properties().Any(p => p.Name == property);
        }

        #endregion
    }

    internal class IndexableItemUser : IIndexableItem
    {
        public JToken Data { get; set; }
        public UserInfo User { get; set; }

        public IndexableItemUser()
        {

        }

        public string GetCreatedByUserId()
        {
            return User.CreatedByUserID.ToString();
        }

        public DateTime GetCreatedOnDate()
        {
            return User.CreatedOnDate;
        }

        public JToken GetData()
        {
            return Data;
        }

        public string GetId()
        {
            return User.UserID.ToString();
        }

        public string GetScope()
        {
            return "DnnUsers";
        }

        public string GetSource()
        {
            return Data.ToString();
        }
    }
}