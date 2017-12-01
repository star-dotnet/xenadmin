/*
 * Copyright (c) Citrix Systems, Inc.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 *   1) Redistributions of source code must retain the above copyright
 *      notice, this list of conditions and the following disclaimer.
 *
 *   2) Redistributions in binary form must reproduce the above
 *      copyright notice, this list of conditions and the following
 *      disclaimer in the documentation and/or other materials
 *      provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
 * COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
 * OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using CookComputing.XmlRpc;
using Newtonsoft.Json;


namespace XenAPI
{
    public partial class Session : XenObject<Session>
    {
        public const int STANDARD_TIMEOUT = 24 * 60 * 60 * 1000;

        /// <summary>
        /// This string is used as the HTTP UserAgent for each request.
        /// </summary>
        public static string UserAgent = string.Format("XenAPI/{0}", Helper.APIVersionString(API_Version.LATEST));

        /// <summary>
        /// If null, no proxy is used, otherwise this proxy is used for each request.
        /// </summary>
        public static IWebProxy Proxy = null;

        public API_Version APIVersion = API_Version.API_1_1;

        private string _uuid;

        public object Tag;

        // Filled in after successful session_login_with_password for version 1.6 or newer connections
        private bool _isLocalSuperuser = true;
        private XenRef<Subject> _subject = null;
        private string _userSid = null;
        private string[] permissions = null;
        private List<Role> roles = new List<Role>();

        #region Constructors

        public Session(int timeout, string url)
        {
            proxy = XmlRpcProxyGen.Create<Proxy>();
            proxy.Url = url;
            proxy.NonStandard = XmlRpcNonStandard.All;
            proxy.Timeout = timeout;
            proxy.UseIndentation = false;
            proxy.UserAgent = UserAgent;
            proxy.KeepAlive = true;
            proxy.Proxy = Proxy;
        }

        public Session(string url)
            : this(STANDARD_TIMEOUT, url)
        {
        }

        public Session(int timeout, string host, int port)
            : this(timeout, GetUrl(host, port))
        {
        }

        public Session(string host, int port)
            : this(STANDARD_TIMEOUT, host, port)
        {
        }

        public Session(string url, string opaqueRef)
            : this(url)
        {
            this._uuid = opaqueRef;
            SetAPIVersion();
            if (APIVersion >= API_Version.API_1_6)
                SetADDetails();
        }

        /// <summary>
        /// Create a new Session instance, using the given instance and timeout.  The connection details and Xen-API session handle will be
        /// copied from the given instance, but a new connection will be created.  Use this if you want a duplicate connection to a host,
        /// for example when you need to cancel an operation that is blocking the primary connection.
        /// </summary>
        /// <param name="session"></param>
        /// <param name="timeout"></param>
        public Session(Session session, int timeout)
            : this(session.Url, timeout)
        {
            _uuid = session.uuid;
            APIVersion = session.APIVersion;
            _isLocalSuperuser = session._isLocalSuperuser;
            _subject = session._subject;
            _userSid = session._userSid;
        }

        #endregion

        // Used after VDI.open_database
        public static Session get_record(Session session, string _session)
        {
            Session newSession = new Session(session.proxy.Url);
            newSession._uuid = _session;
            newSession.SetAPIVersion();
            return newSession;
        }

        private void SetADDetails()
        {
            _isLocalSuperuser = get_is_local_superuser();
            if (IsLocalSuperuser)
                return;

            _subject = get_subject();
            _userSid = get_auth_user_sid();

            // Cache the details of this user to avoid making server calls later
            // For example, some users get access to the pool through a group subject and will not be in the main cache
            UserDetails.UpdateDetails(_userSid, this);

            if (APIVersion <= API_Version.API_1_6)  // Older versions have no RBAC, only AD
                return;

            // allRoles will contain every role on the server, permissions contains the subset of those that are available to this session.
            permissions = Session.get_rbac_permissions(this, uuid);
            Dictionary<XenRef<Role>, Role> allRoles = Role.get_all_records(this);
            // every Role object is either a single api call (a permission) or has subroles and contains permissions through its descendants.
            // We take out the parent Roles (VM-Admin etc.) into the Session.Roles field
            foreach (string s in permissions)
            {
                foreach (XenRef<Role> xr in allRoles.Keys)
                {
                    Role r = allRoles[xr];
                    if (r.subroles.Count > 0 && r.name_label == s)
                    {
                        r.opaque_ref = xr.opaque_ref;
                        roles.Add(r);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Retrieves the current users details from the UserDetails map. These values are only updated when a new session is created.
        /// </summary>
        public virtual UserDetails CurrentUserDetails
        {
            get
            {
                return _userSid == null ? null : UserDetails.Sid_To_UserDetails[_userSid];
            }
        }

        public override void UpdateFrom(Session update)
        {
            throw new Exception("The method or operation is not implemented.");
        }

        public override string SaveChanges(Session session, string _serverOpaqueRef, Session serverObject)
        {
            throw new Exception("The method or operation is not implemented.");
        }

        public Proxy proxy { get; private set; }

        public JsonRpcClient JsonRpcClient { get; private set; }

        public string uuid
        {
            get { return _uuid; }
        }

        public string Url
        {
            get
            {
                if (JsonRpcClient != null)
                    return JsonRpcClient.Url;
                else
                    return proxy.Url;
            }
        }

        public string ConnectionGroupName
        {
            get
            {
                if (JsonRpcClient != null)
                    return JsonRpcClient.ConnectionGroupName;
                else
                    return proxy.ConnectionGroupName;
            }
            set
            {
                if (JsonRpcClient != null)
                    JsonRpcClient.ConnectionGroupName = value;
                else
                    proxy.ConnectionGroupName = value;
            }
        }

        /// <summary>
        /// Always true before API version 1.6.
        /// </summary>
        public virtual bool IsLocalSuperuser
        {
            get { return _isLocalSuperuser; }
        }

        /// <summary>
        /// The OpaqueRef for the Subject under whose authority the current user is logged in;
        /// may correspond to either a group or a user.
        /// Null if IsLocalSuperuser is true.
        /// </summary>
        [JsonConverter(typeof(XenRefConverter<VDI>))]
        public XenRef<Subject> Subject
        {
            get { return _subject; }
        }

        /// <summary>
        /// The Active Directory SID of the currently logged-in user.
        /// Null if IsLocalSuperuser is true.
        /// </summary>
        public string UserSid
        {
            get { return _userSid; }
        }

        /// <summary>
        /// All permissions associated with the session at the time of log in. This is the list xapi uses until the session is logged out;
        /// even if the permitted roles change on the server side, they don't apply until the next session.
        /// </summary>
        public string[] Permissions
        {
            get { return permissions; }
        }

        /// <summary>
        /// All roles associated with the session at the time of log in. Do not rely on roles for determining what a user can do,
        /// instead use Permissions. This list should only be used for UI purposes.
        /// </summary>
        [JsonConverter(typeof(XenRefListConverter<Role>))]
        public List<Role> Roles
        {
            get { return roles; }
        }

        public void login_with_password(string username, string password)
        {
            if (JsonRpcClient != null)
                _uuid = JsonRpcClient.session_login_with_password(username, password);
            else
                _uuid = proxy.session_login_with_password(username, password).parse();

            SetAPIVersion();
        }

        public void login_with_password(string username, string password, string version)
        {
            try
            {
                if (JsonRpcClient != null)
                    _uuid = JsonRpcClient.session_login_with_password(username, password, version);
                else
                    _uuid = proxy.session_login_with_password(username, password, version).parse();

                SetAPIVersion();
                if (APIVersion >= API_Version.API_1_6)
                    SetADDetails();
            }
            catch (Failure exn)
            {
                if (exn.ErrorDescription[0] == Failure.MESSAGE_PARAMETER_COUNT_MISMATCH)
                {
                    // Call the 1.1 version instead.
                    login_with_password(username, password);
                }
                else
                {
                    throw;
                }
            }
        }

        public void login_with_password(string username, string password, string version, string originator)
        {
            try
            {
                if (JsonRpcClient != null)
                    _uuid = JsonRpcClient.session_login_with_password(username, password, version, originator);
                else
                    _uuid = proxy.session_login_with_password(username, password, version, originator).parse();

                SetAPIVersion();
                if (APIVersion >= API_Version.API_1_6)
                    SetADDetails();
            }
            catch (Failure exn)
            {
                if (exn.ErrorDescription[0] == Failure.MESSAGE_PARAMETER_COUNT_MISMATCH)
                {
                    // Call the pre-2.0 version instead.
                    login_with_password(username, password, version);
                }
                else
                {
                    throw;
                }
            }
        }

        public void login_with_password(string username, string password, API_Version version)
        {
            login_with_password(username, password, Helper.APIVersionString(version));
        }

        private void SetAPIVersion()
        {
            Dictionary<XenRef<Pool>, Pool> pools = Pool.get_all_records(this);
            foreach (Pool pool in pools.Values)
            {
                Host host = Host.get_record(this, pool.master);
                APIVersion = Helper.GetAPIVersion(host.API_version_major, host.API_version_minor);
                break;
            }

            //if supported swap endpoints
            JsonRpcClient = null;
            bool isELy = (int)APIVersion == (int)API_Version.API_2_6;
            bool isInvernessOrAbove = (int)APIVersion >= (int)API_Version.API_2_8;

            if (isELy || isInvernessOrAbove)
            {
                JsonRpcClient = new JsonRpcClient(proxy.Url)
                {
                    ConnectionGroupName = proxy.ConnectionGroupName,
                    Timeout = proxy.Timeout,
                    KeepAlive = proxy.KeepAlive,
                    UserAgent = proxy.UserAgent,
                    WebProxy = proxy.Proxy
                };

                if (isInvernessOrAbove)
                    JsonRpcClient.JsonRpcVersion = JsonRpcVersion.v2;

                proxy = null;
            }
        }

        public void slave_local_login_with_password(string username, string password)
        {
            if (JsonRpcClient != null)
                _uuid = JsonRpcClient.session_slave_local_login_with_password(username, password);
            else
                _uuid = proxy.session_slave_local_login_with_password(username, password).parse();
            //assume the latest API
            APIVersion = API_Version.LATEST;
        }

        public void logout()
        {
            logout(this);
        }

        /// <summary>
        /// Log out of the given session2, using this session for the connection.
        /// </summary>
        /// <param name="session2">The session to log out</param>
        public void logout(Session session2)
        {
            logout(session2._uuid);
            session2._uuid = null;
        }

        /// <summary>
        /// Log out of the session with the given reference, using this session for the connection.
        /// </summary>
        /// <param name="_self">The session to log out</param>
        public void logout(string _self)
        {
            if (_self == null)
                return;

            if (JsonRpcClient != null)
                JsonRpcClient.session_logout(_self);
            else
                proxy.session_logout(_self).parse();
        }

        public void local_logout()
        {
            local_logout(this);
        }

        public void local_logout(Session session2)
        {
            local_logout(session2._uuid);
            session2._uuid = null;
        }

        public void local_logout(string session_uuid)
        {
            if (session_uuid == null)
                return;

            if (JsonRpcClient != null)
                JsonRpcClient.session_local_logout(session_uuid);
            else
                proxy.session_local_logout(session_uuid).parse();
        }

        public void change_password(string oldPassword, string newPassword)
        {
            change_password(this, oldPassword, newPassword);
        }

        /// <summary>
        /// Change the password on the given session2, using this session for the connection.
        /// </summary>
        /// <param name="session2">The session to change</param>
        /// <param name="oldPassword"></param>
        /// <param name="newPassword"></param>
        public void change_password(Session session2, string oldPassword, string newPassword)
        {
            if (JsonRpcClient != null)
                JsonRpcClient.session_change_password(session2.uuid, oldPassword, newPassword);
            else
                proxy.session_change_password(session2.uuid, oldPassword, newPassword).parse();
        }

        public string get_this_host()
        {
            return get_this_host(this, uuid);
        }

        public static string get_this_host(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_this_host(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_this_host(session.uuid, _self ?? "").parse();
        }

        public string get_this_user()
        {
            return get_this_user(this, uuid);
        }

        public static string get_this_user(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_this_user(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_this_user(session.uuid, _self ?? "").parse();
        }

        public bool get_is_local_superuser()
        {
            return get_is_local_superuser(this, uuid);
        }

        public static bool get_is_local_superuser(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_is_local_superuser(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_is_local_superuser(session.uuid, _self ?? "").parse();
        }

        public static string[] get_rbac_permissions(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_rbac_permissions(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_rbac_permissions(session.uuid, _self ?? "").parse();
        }

        public DateTime get_last_active()
        {
            return get_last_active(this, uuid);
        }

        public static DateTime get_last_active(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_last_active(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_last_active(session.uuid, _self ?? "").parse();
        }

        public bool get_pool()
        {
            return get_pool(this, uuid);
        }

        public static bool get_pool(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_pool(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_pool(session.uuid, _self ?? "").parse();
        }

        public XenRef<Subject> get_subject()
        {
            return get_subject(this, uuid);
        }

        public static XenRef<Subject> get_subject(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_subject(session.uuid, _self ?? "");
            else
                return new XenRef<Subject>(session.proxy.session_get_subject(session.uuid, _self ?? "").parse());
        }

        public string get_auth_user_sid()
        {
            return get_auth_user_sid(this, uuid);
        }

        public static string get_auth_user_sid(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_auth_user_sid(session.uuid, _self ?? "");
            else
                return session.proxy.session_get_auth_user_sid(session.uuid, _self ?? "").parse();
        }

        #region AD SID enumeration and bootout

        public string[] get_all_subject_identifiers()
        {
            return get_all_subject_identifiers(this);
        }

        public static string[] get_all_subject_identifiers(Session session)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_all_subject_identifiers(session.uuid);
            else
                return session.proxy.session_get_all_subject_identifiers(session.uuid).parse();
        }

        public XenRef<Task> async_get_all_subject_identifiers()
        {
            return async_get_all_subject_identifiers(this);
        }

        public static XenRef<Task> async_get_all_subject_identifiers(Session session)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.async_session_get_all_subject_identifiers(session.uuid);
            else
                return XenRef<Task>.Create(session.proxy.async_session_get_all_subject_identifiers(session.uuid).parse());
        }

        public string logout_subject_identifier(string subject_identifier)
        {
            return logout_subject_identifier(this, subject_identifier);
        }

        public static string logout_subject_identifier(Session session, string subject_identifier)
        {
            if (session.JsonRpcClient != null)
            {
                session.JsonRpcClient.session_logout_subject_identifier(session.uuid, subject_identifier);
                return string.Empty;
            }
            else
                return session.proxy.session_logout_subject_identifier(session.uuid, subject_identifier).parse();
        }

        public XenRef<Task> async_logout_subject_identifier(string subject_identifier)
        {
            return async_logout_subject_identifier(this, subject_identifier);
        }

        public static XenRef<Task> async_logout_subject_identifier(Session session, string subject_identifier)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.async_session_logout_subject_identifier(session.uuid, subject_identifier);
            else
                return XenRef<Task>.Create(session.proxy.async_session_logout_subject_identifier(session.uuid, subject_identifier).parse());
        }

        #endregion

        #region other_config stuff

        public Dictionary<string, string> get_other_config()
        {
            return get_other_config(this, uuid);
        }

        public static Dictionary<string, string> get_other_config(Session session, string _self)
        {
            if (session.JsonRpcClient != null)
                return session.JsonRpcClient.session_get_other_config(session.uuid, _self ?? "");
            else
                return Maps.convert_from_proxy_string_string(session.proxy.session_get_other_config(session.uuid, _self ?? "").parse());
        }

        public void set_other_config(Dictionary<string, string> _other_config)
        {
            set_other_config(this, uuid, _other_config);
        }

        public static void set_other_config(Session session, string _self, Dictionary<string, string> _other_config)
        {
            if (session.JsonRpcClient != null)
                session.JsonRpcClient.session_set_other_config(session.uuid, _self ?? "", _other_config);
            else
                session.proxy.session_set_other_config(session.uuid, _self ?? "", Maps.convert_to_proxy_string_string(_other_config)).parse();
        }

        public void add_to_other_config(string _key, string _value)
        {
            add_to_other_config(this, uuid, _key, _value);
        }

        public static void add_to_other_config(Session session, string _self, string _key, string _value)
        {
            if (session.JsonRpcClient != null)
                session.JsonRpcClient.session_add_to_other_config(session.uuid, _self ?? "", _key ?? "", _value ?? "");
            else
                session.proxy.session_add_to_other_config(session.uuid, _self ?? "", _key ?? "", _value ?? "").parse();
        }

        public void remove_from_other_config(string _key)
        {
            remove_from_other_config(this, uuid, _key);
        }

        public static void remove_from_other_config(Session session, string _self, string _key)
        {
            if (session.JsonRpcClient != null)
                session.JsonRpcClient.session_remove_from_other_config(session.uuid, _self ?? "", _key ?? "");
            else
                session.proxy.session_remove_from_other_config(session.uuid, _self ?? "", _key ?? "").parse();
        }

        #endregion

        static Session()
        {
            //ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;
        }

        private static string GetUrl(string hostname, int port)
        {
            return string.Format("{0}://{1}:{2}", port == 8080 || port == 80 ? "http" : "https", hostname, port);
        }

        private static bool ValidateServerCertificate(
              object sender,
              X509Certificate certificate,
              X509Chain chain,
              SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }
    }
}
