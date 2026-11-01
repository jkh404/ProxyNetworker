const tabs = [
  { key: 'overview', title: '总览', icon: 'dashboard', group: '控制台', description: '运行状态与资源分布', code: 'OV-01' },
  { key: 'users', title: '用户', icon: 'users', group: '账户与资源', adminOnly: true, description: '账号、角色与资源配额', code: 'US-02' },
  { key: 'virtualNetworks', title: '虚拟局域网', icon: 'network', group: '账户与资源', description: '私有网络与网关配置', code: 'VN-03' },
  { key: 'portTunnels', title: '内网穿透', icon: 'route', group: '账户与资源', description: 'TCP 与 UDP 端口映射', code: 'PT-04' },
  { key: 'tokens', title: '访问 Token', icon: 'key', group: '账户与资源', description: '资源访问凭据与有效期', code: 'TK-05' },
  { key: 'settings', title: '系统设置', icon: 'settings', group: '系统', adminOnly: true, description: '端口边界与全局流量限制', code: 'ST-06' }
];

const navGroups = ['控制台', '账户与资源', '系统'];
const routeAliases = { 'virtual-networks': 'virtualNetworks', 'port-tunnels': 'portTunnels', 'access-tokens': 'tokens', token: 'tokens' };
const permanentTokenTypes = [{ value: 'Permanent', label: '永久' }, { value: 'Limited', label: '限期' }];
const virtualNetworkTokenTypes = [...permanentTokenTypes, { value: 'OneTime', label: '一次性' }];

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options
  });
  if (response.status === 204) return null;
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(data?.message || data?.title || `HTTP ${response.status}`);
  return data;
}

function fmtDate(value, locale = 'zh-CN') {
  return value ? new Date(value).toLocaleString(locale, { hour12: false }) : '—';
}

function fmtBytes(value) {
  if (!value) return '不限';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = Number(value);
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index++;
  }
  return `${size.toFixed(index ? 1 : 0)} ${units[index]}`;
}

function fmtTrafficBytes(value) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = Math.max(0, Number(value) || 0);
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index++;
  }
  return `${size.toFixed(index ? 1 : 0)} ${units[index]}`;
}

function fmtTrafficSpeed(value) {
  return `${fmtTrafficBytes(value)}/s`;
}

function compactLabel(value, maxLength = 22) {
  const text = String(value || '').trim();
  return text.length <= maxLength ? text : `${text.slice(0, maxLength - 1)}…`;
}

function normalizeTab(tab) {
  const value = String(tab || '').replace(/^#\/?/, '').trim();
  return value ? routeAliases[value] || value : 'overview';
}

function routeTab() {
  return normalizeTab(decodeURIComponent(location.hash || ''));
}

function findTab(key) {
  return tabs.find(item => item.key === key);
}

function isTabAllowed(tab, me) {
  const definition = findTab(tab);
  return Boolean(definition) && (!definition.adminOnly || me?.role === 'Admin');
}

function setRouteTab(tab, replace = false) {
  const nextHash = `#${tab}`;
  if (location.hash === nextHash) return;
  if (replace) history.replaceState(null, '', nextHash);
  else history.pushState(null, '', nextHash);
}

function selectNodeText(node) {
  if (!node) return;
  const selection = window.getSelection();
  if (!selection) return;
  const range = document.createRange();
  range.selectNodeContents(node);
  selection.removeAllRanges();
  selection.addRange(range);
}

function defaultLoginForm() { return { username: '', password: '' }; }
function defaultUserForm(settings = null) {
  return {
    username: '',
    password: '',
    role: 'User',
    maxVirtualNetworks: 1,
    maxPortTunnels: 3,
    portRangeStart: settings?.publicPortRangeStart ?? 1,
    portRangeEnd: settings?.publicPortRangeEnd ?? 65535,
    bandwidthLimitBytes: 0,
    maxTrafficSpeedBytesPerSecond: 0,
    isDisabled: false
  };
}
function defaultVirtualNetworkForm() { return { name: '', gatewayAddress: '10.66.0.1', prefixLength: 24, transportProtocol: 'udp', encryptionEnabled: true }; }
function defaultPortTunnelForm() { return { name: '', protocol: 'tcp', privateHost: '127.0.0.1', privatePort: null }; }
function defaultTokenForm() { return { tokenType: 'Permanent', validFrom: '', validUntil: '' }; }
function defaultPasswordForm() { return { currentPassword: '', newPassword: '' }; }
function defaultPasswordResetForm() { return { newPassword: '', confirmPassword: '' }; }
function defaultSettingsForm() { return { publicPortRangeStart: 1, publicPortRangeEnd: 65535, maxBandwidthLimitBytes: 0, maxTrafficSpeedBytesPerSecond: 0 }; }
function defaultConfirm() { return { action: '', payload: null, title: '', message: '', confirmLabel: '删除', requireText: '', input: '' }; }

const { createApp } = window.Vue;
const { translateDocument } = window.ProxyNetworkerI18n;

createApp({
  data() {
    return {
      sessionChecked: false,
      language: localStorage.getItem('pn-ui-language') === 'en-US' ? 'en-US' : 'zh-CN',
      translationObserver: null,
      me: null,
      users: [],
      virtualNetworks: [],
      detailsNetwork: null,
      virtualNetworkStatus: null,
      virtualNetworkTopology: null,
      networkDetailsLoading: false,
      topologyLoading: false,
      topologyTimer: null,
      virtualNetworkProtocolDraft: 'udp',
      virtualNetworkEncryptionDraft: true,
      tokenAddressDrafts: {},
      portTunnels: [],
      runtimeTunnels: [],
      tokens: [],
      settings: null,
      plainToken: '',
      selectedTab: 'overview',
      selectedTokenKind: 'VirtualNetwork',
      selectedTokenResourceId: null,
      loginError: '',
      statusMessage: '',
      statusIsError: false,
      statusTimer: null,
      loginBusy: false,
      loginShowPassword: false,
      isRefreshing: false,
      mobileNavOpen: false,
      activeDialog: null,
      dialogBusy: false,
      lastUpdated: null,
      searchUsers: '',
      searchNetworks: '',
      searchTunnels: '',
      searchTokens: '',
      editingUser: null,
      resettingUser: null,
      confirmDialog: defaultConfirm(),
      loginForm: defaultLoginForm(),
      userForm: defaultUserForm(),
      virtualNetworkForm: defaultVirtualNetworkForm(),
      portTunnelForm: defaultPortTunnelForm(),
      tokenForm: defaultTokenForm(),
      passwordForm: defaultPasswordForm(),
      passwordResetForm: defaultPasswordResetForm(),
      settingsForm: defaultSettingsForm()
    };
  },

  computed: {
    isAdmin() { return this.me?.role === 'Admin'; },
    visibleNavSections() {
      return navGroups.map(label => ({
        label,
        items: tabs.filter(tab => tab.group === label && (!tab.adminOnly || this.isAdmin))
      })).filter(section => section.items.length);
    },
    currentTab() { return findTab(this.selectedTab) || tabs[0]; },
    pageTitle() { return this.currentTab.title; },
    pageDescription() { return this.currentTab.description; },
    pageCode() { return this.currentTab.code; },
    primaryActionLabel() {
      return { users: '新建用户', virtualNetworks: '新建网络', portTunnels: '新建通道' }[this.selectedTab] || '';
    },
    enabledUserCount() { return this.users.filter(user => !user.isDisabled).length; },
    tokenTypeSummary() {
      const usable = this.tokens.filter(token => !token.isConsumed).length;
      return `${usable} 个可用`;
    },
    filteredUsers() {
      const query = this.searchUsers.toLocaleLowerCase();
      return this.users.filter(user => !query || [user.username, user.role, user.id].some(value => String(value || '').toLocaleLowerCase().includes(query)));
    },
    filteredVirtualNetworks() {
      const query = this.searchNetworks.toLocaleLowerCase();
      return this.virtualNetworks.filter(item => !query || [item.name, item.gatewayAddress, item.id, this.ownerName(item.ownerUserId)].some(value => String(value || '').toLocaleLowerCase().includes(query)));
    },
    filteredPortTunnels() {
      const query = this.searchTunnels.toLocaleLowerCase();
      return this.portTunnels.filter(item => !query || [item.name, item.protocol, item.privateHost, item.publicPort, item.id].some(value => String(value || '').toLocaleLowerCase().includes(query)));
    },
    filteredTokens() {
      const query = this.searchTokens.toLocaleLowerCase();
      return this.tokens.filter(item => !query || [item.tokenPreview, item.tokenType, item.id].some(value => String(value || '').toLocaleLowerCase().includes(query)));
    },
    userPortMin() { return this.settings?.publicPortRangeStart ?? 1; },
    userPortMax() { return this.settings?.publicPortRangeEnd ?? 65535; },
    bandwidthMax() { return this.settings?.maxBandwidthLimitBytes > 0 ? this.settings.maxBandwidthLimitBytes : null; },
    speedMax() { return this.settings?.maxTrafficSpeedBytesPerSecond > 0 ? this.settings.maxTrafficSpeedBytesPerSecond : null; },
    tokenResourceItems() { return this.selectedTokenKind === 'VirtualNetwork' ? this.virtualNetworks : this.portTunnels; },
    tokenTypeOptions() { return this.selectedTokenKind === 'VirtualNetwork' ? virtualNetworkTokenTypes : permanentTokenTypes; },
    topologyGraphNodes() {
      const nodes = this.virtualNetworkTopology?.nodes || [];
      const server = nodes.find(node => node.role === 'server');
      const clients = nodes.filter(node => node.role !== 'server');
      const serverPoint = { x: 500, y: 280 };
      const positions = new Map();
      if (server) positions.set(server.virtualAddress, {
        ...server,
        ...serverPoint,
        label: compactLabel(server.nodeName || server.machineName || 'Server', 18),
        labelY: serverPoint.y + 70,
        addressY: serverPoint.y + 87
      });

      const radiusX = clients.length > 8 ? 385 : 350;
      const radiusY = clients.length > 8 ? 220 : 205;
      clients.forEach((node, index) => {
        const angle = -Math.PI / 2 + (index * Math.PI * 2 / Math.max(clients.length, 1));
        const point = {
          ...node,
          x: serverPoint.x + Math.cos(angle) * radiusX,
          y: serverPoint.y + Math.sin(angle) * radiusY,
          label: compactLabel(node.nodeName || node.machineName || node.virtualAddress, 18),
          labelY: -1,
          addressY: -1
        };
        point.labelY = point.y > 360 ? point.y - 58 : point.y + 62;
        point.addressY = point.y > 360 ? point.y - 42 : point.y + 78;
        positions.set(node.virtualAddress, point);
      });

      return [...positions.values()];
    },
    topologyGraphEdges() {
      const nodes = new Map(this.topologyGraphNodes.map(node => [node.virtualAddress, node]));
      const sourceEdges = this.virtualNetworkTopology?.edges || [];
      return sourceEdges.map((edge, index) => {
        const source = nodes.get(edge.sourceAddress);
        const destination = nodes.get(edge.destinationAddress);
        if (!source || !destination) return null;
        const dx = destination.x - source.x;
        const dy = destination.y - source.y;
        const distance = Math.max(1, Math.hypot(dx, dy));
        const sourceRadius = source.role === 'server' ? 48 : 36;
        const destinationRadius = destination.role === 'server' ? 48 : 36;
        const unitX = dx / distance;
        const unitY = dy / distance;
        const totalBytes = (Number(edge.bytesSent) || 0) + (Number(edge.bytesReceived) || 0);
        const currentBytesPerSecond = (Number(edge.bytesPerSecondSent) || 0) + (Number(edge.bytesPerSecondReceived) || 0);
        const isDirect = edge.connectionType === 'direct';
        const latency = this.topologyEdgeLatency(edge);
        const label = `${latency} · ${currentBytesPerSecond > 0 ? fmtTrafficSpeed(currentBytesPerSecond) : fmtTrafficBytes(totalBytes)}`;
        return {
          ...edge,
          isDirect,
          x1: source.x + unitX * sourceRadius,
          y1: source.y + unitY * sourceRadius,
          x2: destination.x - unitX * destinationRadius,
          y2: destination.y - unitY * destinationRadius,
          labelX: (source.x + destination.x) / 2,
          labelY: (source.y + destination.y) / 2,
          labelWidth: Math.max(68, label.length * 7.2 + 18),
          label,
          showLabel: isDirect || sourceEdges.length <= 5,
          motionPath: `M ${source.x + unitX * sourceRadius} ${source.y + unitY * sourceRadius} L ${destination.x - unitX * destinationRadius} ${destination.y - unitY * destinationRadius}`,
          motionDuration: isDirect ? `${3.2 + (index % 3) * 0.35}s` : `${5.6 + (index % 4) * 0.45}s`,
          motionBegin: `-${(index % 5) * 0.65}s`
        };
      }).filter(Boolean);
    },
    topologyDirectCount() { return (this.virtualNetworkTopology?.edges || []).filter(edge => edge.connectionType === 'direct').length; },
    topologyRelayCount() { return (this.virtualNetworkTopology?.edges || []).filter(edge => edge.connectionType === 'relay').length; },
    topologyTotalTraffic() {
      return (this.virtualNetworkTopology?.edges || []).reduce((total, edge) => total + (Number(edge.bytesSent) || 0) + (Number(edge.bytesReceived) || 0), 0);
    },
    topologyCurrentSpeed() {
      return (this.virtualNetworkTopology?.edges || []).reduce((total, edge) => total + (Number(edge.bytesPerSecondSent) || 0) + (Number(edge.bytesPerSecondReceived) || 0), 0);
    },
    userEditor() { return this.activeDialog === 'userEdit' ? this.editingUser : this.userForm; },
    isDrawerOpen() { return ['userCreate', 'userEdit', 'virtualNetworkCreate', 'portTunnelCreate'].includes(this.activeDialog); },
    isCenterDialogOpen() { return ['password', 'resetPassword', 'confirm', 'virtualNetworkDetails'].includes(this.activeDialog); },
    dialogKicker() {
      return { userCreate: 'ACCOUNT SETUP', userEdit: 'ACCOUNT CONTROL', virtualNetworkCreate: 'NEW RELAY NETWORK', portTunnelCreate: 'NEW PORT ROUTE', virtualNetworkDetails: 'NETWORK OBSERVABILITY', password: 'ACCOUNT SECURITY', resetPassword: 'CREDENTIAL RESET', confirm: 'DESTRUCTIVE ACTION' }[this.activeDialog] || '';
    },
    dialogTitle() {
      return { userCreate: '新建用户', userEdit: this.editingUser?.username || '编辑用户', virtualNetworkCreate: '新建虚拟局域网', portTunnelCreate: '新建内网穿透', virtualNetworkDetails: this.detailsNetwork?.name || '虚拟局域网详情', password: '修改密码', resetPassword: '重置密码', confirm: this.confirmDialog.title }[this.activeDialog] || '';
    },
    dialogDescription() {
      return { userCreate: '建立账号并分配资源边界。', userEdit: '调整角色、配额和账号状态。', virtualNetworkCreate: '定义网络名称、网关和地址前缀。', portTunnelCreate: '将一个内网服务映射到公网端口。', virtualNetworkDetails: '查看在线设备、访问 Token 与连接历史。', password: '更新当前账号的登录密码。', resetPassword: '管理员将为该账号设置新密码。', confirm: '此操作无法撤销，请确认后继续。' }[this.activeDialog] || '';
    },
    confirmDisabled() {
      if (this.dialogBusy) return true;
      return Boolean(this.confirmDialog.requireText && this.confirmDialog.input !== this.confirmDialog.requireText);
    },
    lastUpdatedLabel() {
      const prefix = this.language === 'en-US' ? 'SYNC' : '同步';
      if (!this.lastUpdated) return `${prefix} --:--:--`;
      return `${prefix} ${this.lastUpdated.toLocaleTimeString(this.language, { hour12: false })}`;
    }
  },

  async mounted() {
    window.addEventListener('hashchange', this.onHashChange);
    window.addEventListener('keydown', this.onKeydown);
    this.applyLanguage();
    this.translationObserver = new MutationObserver(() => this.applyLanguage());
    this.translationObserver.observe(document.body, {
      childList: true,
      subtree: true,
      characterData: true,
      attributes: true,
      attributeFilter: ['placeholder', 'aria-label', 'title']
    });
    await this.loadSession();
  },

  unmounted() {
    window.removeEventListener('hashchange', this.onHashChange);
    window.removeEventListener('keydown', this.onKeydown);
    if (this.statusTimer) clearTimeout(this.statusTimer);
    this.stopTopologyPolling();
    this.translationObserver?.disconnect();
    this.translationObserver = null;
  },

  methods: {
    fmtDate(value) { return fmtDate(value, this.language); },
    fmtBytes,
    fmtTrafficBytes,
    fmtTrafficSpeed,

    resourceKindLabel(value) {
      const labels = {
        'virtual-network': this.language === 'en-US' ? 'Virtual network' : '虚拟局域网',
        'port-tunnel': this.language === 'en-US' ? 'Port tunnel' : '内网穿透'
      };
      return labels[value] || value || '—';
    },

    protocolLabel(value) {
      return value ? String(value).toUpperCase() : '—';
    },

    runtimeProtocol(item) {
      if (item?.kind === 'virtual-network') {
        return this.virtualNetworks.find(network => network.id === item.id)?.transportProtocol || item.protocol;
      }
      return item?.protocol;
    },

    runtimeStatusLabel(value) {
      const labels = this.language === 'en-US'
        ? { running: 'Running', stopped: 'Stopped', online: 'Online', offline: 'Offline' }
        : { running: '运行中', stopped: '已停止', online: '在线', offline: '离线' };
      return labels[String(value || '').toLowerCase()] || value || '—';
    },

    trafficTotalLabel(value) {
      return `${fmtTrafficBytes(value)} ${this.language === 'en-US' ? 'total' : '累计'}`;
    },

    initials(username) {
      const value = String(username || 'PN').trim();
      return value.slice(0, 2).toLocaleUpperCase();
    },

    shortId(id) {
      return String(id || '').slice(0, 8);
    },

    tokenTypeLabel(value) {
      const labels = this.language === 'en-US'
        ? { Permanent: 'Permanent', Limited: 'Limited', OneTime: 'One-time' }
        : { Permanent: '永久', Limited: '限期', OneTime: '一次性' };
      return labels[value] || value || '—';
    },

    fmtQuota(value) {
      return Number(value) >= 2147483647 ? (this.language === 'en-US' ? 'Unlimited' : '不限') : value;
    },

    tokenValidityLabel(token) {
      if (!token?.validFrom && !token?.validUntil) return this.language === 'en-US' ? 'Permanently valid' : '永久有效';
      return `${this.fmtDate(token.validFrom)} — ${this.fmtDate(token.validUntil)}`;
    },

    setLanguage(language) {
      this.language = language === 'en-US' ? 'en-US' : 'zh-CN';
      localStorage.setItem('pn-ui-language', this.language);
      this.statusMessage = '';
      this.applyLanguage();
    },

    applyLanguage() {
      document.documentElement.lang = this.language;
      translateDocument(document.body, this.language);
      document.title = this.language === 'en-US' ? 'ProxyNetworker · Console' : 'ProxyNetworker · 控制台';
    },

    navCount(key) {
      if (key === 'users') return this.isAdmin ? this.users.length : null;
      if (key === 'virtualNetworks') return this.virtualNetworks.length;
      if (key === 'portTunnels') return this.portTunnels.length;
      if (key === 'tokens') return this.tokens.length;
      return null;
    },

    setStatus(message, isError = false) {
      if (this.statusTimer) clearTimeout(this.statusTimer);
      this.statusMessage = message || '';
      this.statusIsError = isError;
      if (message) {
        this.statusTimer = setTimeout(() => {
          this.statusMessage = '';
          this.statusTimer = null;
        }, 4200);
      }
    },

    setInitialRoute() {
      const tab = routeTab();
      this.selectedTab = isTabAllowed(tab, this.me) ? tab : 'overview';
      if (location.hash && routeTab() !== this.selectedTab) setRouteTab(this.selectedTab, true);
    },

    async loadSession() {
      try {
        this.me = await api('/api/auth/me');
        this.setInitialRoute();
        await this.refreshAll();
      } catch {
        this.me = null;
      } finally {
        this.sessionChecked = true;
      }
    },

    async refreshAll() {
      if (!this.me) return;
      if (this.isRefreshing) return;
      this.isRefreshing = true;
      try {
        const calls = [
          api('/api/virtual-networks').then(data => { this.virtualNetworks = data; }),
          api('/api/port-tunnels').then(data => { this.portTunnels = data; })
        ];

        if (this.isAdmin) {
          calls.push(api('/api/users').then(data => { this.users = data; }));
          calls.push(api('/api/runtime-tunnels').then(data => { this.runtimeTunnels = data; }));
          calls.push(api('/api/settings').then(data => {
            const hadSettings = Boolean(this.settings);
            this.settings = data;
            this.settingsForm = { ...data };
            if (!hadSettings) this.userForm = defaultUserForm(data);
          }));
        } else {
          this.users = [this.me];
          this.runtimeTunnels = [];
          this.settings = null;
        }

        await Promise.all(calls);
        this.reconcileSelectedTokenResource();
        this.ensureTokenTypeAllowed();
        await this.loadSelectedTokens();
        this.lastUpdated = new Date();

        if (!isTabAllowed(this.selectedTab, this.me)) this.switchTab('overview', { replaceRoute: true });
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.isRefreshing = false;
      }
    },

    async login() {
      this.loginBusy = true;
      this.loginError = '';
      try {
        await api('/api/auth/login', { method: 'POST', body: JSON.stringify(this.loginForm) });
        this.loginForm = defaultLoginForm();
        await this.loadSession();
      } catch (error) {
        this.loginError = error.message;
        this.setStatus(error.message, true);
      } finally {
        this.loginBusy = false;
      }
    },

    async logout() {
      try {
        await api('/api/auth/logout', { method: 'POST' });
        location.reload();
      } catch (error) {
        this.setStatus(error.message, true);
      }
    },

    switchTab(tab, options = {}) {
      const normalized = normalizeTab(tab);
      this.selectedTab = isTabAllowed(normalized, this.me) ? normalized : 'overview';
      this.mobileNavOpen = false;
      this.activeDialog = null;
      if (options.updateRoute ?? true) setRouteTab(this.selectedTab, options.replaceRoute ?? false);
    },

    onHashChange() {
      if (!this.me) return;
      const tab = routeTab();
      if (isTabAllowed(tab, this.me)) this.switchTab(tab, { updateRoute: false });
      else this.switchTab('overview', { replaceRoute: true });
    },

    onKeydown(event) {
      if (event.key !== 'Escape') return;
      if (this.activeDialog && !this.dialogBusy) this.closeDialog();
      else if (this.mobileNavOpen) this.mobileNavOpen = false;
    },

    openDialog(name) {
      this.dialogBusy = false;
      this.activeDialog = name;
      this.mobileNavOpen = false;
      this.$nextTick(() => {
        const target = document.querySelector('.dialog-layer input:not([disabled]), .dialog-layer select');
        target?.focus();
      });
    },

    closeDialog() {
      if (this.dialogBusy) return;
      this.stopTopologyPolling();
      this.activeDialog = null;
      this.detailsNetwork = null;
      this.virtualNetworkStatus = null;
      this.virtualNetworkTopology = null;
      this.tokenAddressDrafts = {};
      this.editingUser = null;
      this.resettingUser = null;
      this.confirmDialog = defaultConfirm();
    },

    openPrimaryCreate() {
      if (this.selectedTab === 'users') this.openCreateUser();
      else if (this.selectedTab === 'virtualNetworks') this.openVirtualNetworkCreate();
      else if (this.selectedTab === 'portTunnels') this.openPortTunnelCreate();
    },

    openCreateUser() {
      this.userForm = defaultUserForm(this.settings);
      this.openDialog('userCreate');
    },

    openEditUser(user) {
      this.editingUser = { ...user };
      this.openDialog('userEdit');
    },

    openResetPassword(user) {
      this.resettingUser = user;
      this.passwordResetForm = defaultPasswordResetForm();
      this.openDialog('resetPassword');
    },

    openVirtualNetworkCreate() {
      this.virtualNetworkForm = defaultVirtualNetworkForm();
      this.openDialog('virtualNetworkCreate');
    },

    async openVirtualNetworkDetails(item) {
      this.stopTopologyPolling();
      this.detailsNetwork = item;
      this.virtualNetworkStatus = null;
      this.virtualNetworkTopology = null;
      this.tokenAddressDrafts = {};
      this.virtualNetworkProtocolDraft = item.transportProtocol || 'udp';
      this.virtualNetworkEncryptionDraft = item.encryptionEnabled !== false;
      this.openDialog('virtualNetworkDetails');
      await this.refreshVirtualNetworkDetails(item.id);
      if (this.activeDialog === 'virtualNetworkDetails' && this.detailsNetwork?.id === item.id) {
        this.startTopologyPolling();
      }
    },

    async loadVirtualNetworkStatus(id) {
      if (!id || this.networkDetailsLoading) return;
      this.networkDetailsLoading = true;
      try {
        this.virtualNetworkStatus = await api(`/api/virtual-networks/${id}/status`);
        const drafts = {};
        for (const token of this.virtualNetworkStatus?.tokens || []) {
          drafts[token.id] = token.assignedVirtualAddress || '';
        }
        this.tokenAddressDrafts = drafts;
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.networkDetailsLoading = false;
      }
    },

    async loadVirtualNetworkTopology(id, silent = false) {
      if (!id || this.topologyLoading) return;
      this.topologyLoading = true;
      try {
        this.virtualNetworkTopology = await api(`/api/virtual-networks/${id}/topology`);
      } catch (error) {
        if (!silent) this.setStatus(error.message, true);
      } finally {
        this.topologyLoading = false;
      }
    },

    async refreshVirtualNetworkDetails(id) {
      const networkId = id || this.detailsNetwork?.id;
      if (!networkId) return;
      await Promise.all([
        this.loadVirtualNetworkStatus(networkId),
        this.loadVirtualNetworkTopology(networkId)
      ]);
    },

    startTopologyPolling() {
      this.stopTopologyPolling();
      this.topologyTimer = setInterval(() => {
        if (this.activeDialog === 'virtualNetworkDetails' && !this.dialogBusy && this.detailsNetwork?.id) {
          this.loadVirtualNetworkTopology(this.detailsNetwork.id, true);
        }
      }, 5000);
    },

    stopTopologyPolling() {
      if (this.topologyTimer) clearInterval(this.topologyTimer);
      this.topologyTimer = null;
    },

    openPortTunnelCreate() {
      this.portTunnelForm = defaultPortTunnelForm();
      this.openDialog('portTunnelCreate');
    },

    openPasswordDialog() {
      this.passwordForm = defaultPasswordForm();
      this.openDialog('password');
    },

    ownerName(id) {
      return this.users.find(user => user.id === id)?.username || this.shortId(id);
    },

    tokenResourcePath(kind, id) {
      return kind === 'VirtualNetwork' ? `/api/virtual-networks/${id}/tokens` : `/api/port-tunnels/${id}/tokens`;
    },

    resourcePath(kind, id) {
      return kind === 'VirtualNetwork' ? `/api/virtual-networks/${id}` : `/api/port-tunnels/${id}`;
    },

    resourceLabel(kind) {
      return kind === 'VirtualNetwork' ? '虚拟局域网' : '内网穿透';
    },

    reconcileSelectedTokenResource() {
      const items = this.tokenResourceItems;
      if (!items.length) {
        this.selectedTokenResourceId = null;
        return null;
      }
      if (!this.selectedTokenResourceId || !items.some(item => item.id === this.selectedTokenResourceId)) {
        this.selectedTokenResourceId = items[0].id;
      }
      return this.selectedTokenResourceId;
    },

    ensureTokenTypeAllowed() {
      if (!this.tokenTypeOptions.some(item => item.value === this.tokenForm.tokenType)) {
        this.tokenForm.tokenType = this.tokenTypeOptions[0].value;
      }
    },

    async loadSelectedTokens() {
      const id = this.reconcileSelectedTokenResource();
      if (!id) {
        this.tokens = [];
        return;
      }
      this.tokens = await api(this.tokenResourcePath(this.selectedTokenKind, id));
    },

    async onTokenKindChanged() {
      try {
        this.selectedTokenResourceId = null;
        this.hidePlainToken();
        this.ensureTokenTypeAllowed();
        this.reconcileSelectedTokenResource();
        await this.loadSelectedTokens();
      } catch (error) {
        this.setStatus(error.message, true);
      }
    },

    async onTokenResourceChanged() {
      try {
        this.hidePlainToken();
        await this.loadSelectedTokens();
      } catch (error) {
        this.setStatus(error.message, true);
      }
    },

    async submitUser() {
      const editor = this.userEditor;
      if (!editor) return;
      this.dialogBusy = true;
      try {
        if (this.activeDialog === 'userEdit') {
          await api(`/api/users/${editor.id}`, {
            method: 'PUT',
            body: JSON.stringify({
              role: editor.role,
              isDisabled: editor.isDisabled,
              maxVirtualNetworks: Number(editor.maxVirtualNetworks),
              maxPortTunnels: Number(editor.maxPortTunnels),
              portRangeStart: Number(editor.portRangeStart),
              portRangeEnd: Number(editor.portRangeEnd),
              bandwidthLimitBytes: Number(editor.bandwidthLimitBytes),
              maxTrafficSpeedBytesPerSecond: Number(editor.maxTrafficSpeedBytesPerSecond)
            })
          });
        } else {
          await api('/api/users', { method: 'POST', body: JSON.stringify(editor) });
          this.userForm = defaultUserForm(this.settings);
        }
        const message = this.activeDialog === 'userEdit' ? '用户配置已保存' : '用户已创建';
        this.activeDialog = null;
        this.editingUser = null;
        await this.refreshAll();
        this.setStatus(message);
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async submitResetPassword() {
      if (!this.resettingUser) return;
      if (!this.passwordResetForm.newPassword) {
        this.setStatus('请输入新密码', true);
        return;
      }
      if (this.passwordResetForm.newPassword !== this.passwordResetForm.confirmPassword) {
        this.setStatus('两次输入的密码不一致', true);
        return;
      }
      this.dialogBusy = true;
      try {
        await api(`/api/users/${this.resettingUser.id}/reset-password`, {
          method: 'POST',
          body: JSON.stringify({ newPassword: this.passwordResetForm.newPassword })
        });
        this.activeDialog = null;
        this.resettingUser = null;
        this.passwordResetForm = defaultPasswordResetForm();
        this.setStatus('密码已重置');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    requestDeleteUser(user) {
      this.confirmDialog = {
        action: 'deleteUser',
        payload: user,
        title: '删除用户',
        message: `删除“${user.username}”后，该账号拥有的虚拟局域网、内网穿透和 Token 将一并删除。`,
        confirmLabel: '删除用户',
        requireText: user.username,
        input: ''
      };
      this.openDialog('confirm');
    },

    requestDeleteToken(token) {
      this.confirmDialog = {
        action: 'deleteToken',
        payload: token,
        title: '删除 Token',
        message: `Token ${token.tokenPreview || this.shortId(token.id)} 将立即失效。`,
        confirmLabel: '删除 Token',
        requireText: '',
        input: ''
      };
      this.openDialog('confirm');
    },

    requestDeleteResource(kind, id, name) {
      const label = this.resourceLabel(kind);
      this.confirmDialog = {
        action: 'deleteResource',
        payload: { kind, id, name },
        title: `删除${label}`,
        message: `删除“${name || this.shortId(id)}”后，其关联 Token 将一并失效。`,
        confirmLabel: `删除${label}`,
        requireText: '',
        input: ''
      };
      this.openDialog('confirm');
    },

    async confirmDestructive() {
      const state = this.confirmDialog;
      this.dialogBusy = true;
      try {
        if (state.action === 'deleteUser') {
          await api(`/api/users/${state.payload.id}`, { method: 'DELETE' });
          this.hidePlainToken();
          this.activeDialog = null;
          await this.refreshAll();
          this.setStatus('用户已删除');
        } else if (state.action === 'deleteToken') {
          await api(`/api/access-tokens/${state.payload.id}`, { method: 'DELETE' });
          this.hidePlainToken();
          this.activeDialog = null;
          await this.loadSelectedTokens();
          this.setStatus('Token 已删除');
        } else if (state.action === 'deleteResource') {
          const { kind, id } = state.payload;
          await api(this.resourcePath(kind, id), { method: 'DELETE' });
          if (this.selectedTokenKind === kind && this.selectedTokenResourceId === id) {
            this.selectedTokenResourceId = null;
            this.tokens = [];
            this.hidePlainToken();
          }
          this.activeDialog = null;
          await this.refreshAll();
          this.setStatus(`${this.resourceLabel(kind)}已删除`);
        }
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async createVirtualNetwork() {
      this.dialogBusy = true;
      try {
        await api('/api/virtual-networks', { method: 'POST', body: JSON.stringify(this.virtualNetworkForm) });
        this.virtualNetworkForm = defaultVirtualNetworkForm();
        this.activeDialog = null;
        await this.refreshAll();
        this.setStatus('虚拟局域网已创建');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async toggleVirtualNetwork(item) {
      this.dialogBusy = true;
      try {
        const updated = await api(`/api/virtual-networks/${item.id}/enabled`, {
          method: 'PUT',
          body: JSON.stringify({ enabled: !item.isEnabled })
        });
        await this.refreshAll();
        if (this.detailsNetwork?.id === item.id) {
          this.detailsNetwork = updated;
          await this.refreshVirtualNetworkDetails(item.id);
        }
        this.setStatus(updated.isEnabled ? '虚拟局域网已启用' : '虚拟局域网已停用');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async changeVirtualNetworkProtocol() {
      if (!this.detailsNetwork || this.dialogBusy) return;
      const nextProtocol = this.virtualNetworkProtocolDraft;
      if (nextProtocol === (this.detailsNetwork.transportProtocol || 'udp')) return;

      this.dialogBusy = true;
      try {
        const updated = await api(`/api/virtual-networks/${this.detailsNetwork.id}/protocol`, {
          method: 'PUT',
          body: JSON.stringify({ transportProtocol: nextProtocol })
        });
        this.detailsNetwork = updated;
        this.virtualNetworkProtocolDraft = updated.transportProtocol;
        await this.refreshAll();
        await this.refreshVirtualNetworkDetails(updated.id);
        this.setStatus(this.language === 'en-US'
          ? `Transport protocol switched to ${String(updated.transportProtocol || '').toUpperCase()}`
          : `通信协议已切换为 ${String(updated.transportProtocol || '').toUpperCase()}`);
      } catch (error) {
        this.virtualNetworkProtocolDraft = this.detailsNetwork.transportProtocol || 'udp';
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async changeVirtualNetworkEncryption() {
      if (!this.detailsNetwork || this.dialogBusy) return;
      const nextValue = Boolean(this.virtualNetworkEncryptionDraft);
      if (nextValue === (this.detailsNetwork.encryptionEnabled !== false)) return;

      this.dialogBusy = true;
      try {
        const updated = await api(`/api/virtual-networks/${this.detailsNetwork.id}/encryption`, {
          method: 'PUT',
          body: JSON.stringify({ encryptionEnabled: nextValue })
        });
        this.detailsNetwork = updated;
        this.virtualNetworkEncryptionDraft = updated.encryptionEnabled !== false;
        await this.refreshAll();
        await this.refreshVirtualNetworkDetails(updated.id);
        this.setStatus(this.language === 'en-US'
          ? (updated.encryptionEnabled ? 'Data encryption enabled' : 'Data encryption disabled; HMAC integrity verification remains active')
          : (updated.encryptionEnabled ? '数据加密已启用' : '数据加密已关闭，仍保留 HMAC 完整性校验'));
      } catch (error) {
        this.virtualNetworkEncryptionDraft = this.detailsNetwork.encryptionEnabled !== false;
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    tokenAddressDirty(token) {
      return String(this.tokenAddressDrafts[token.id] || '').trim() !== String(token.assignedVirtualAddress || '');
    },

    async updateVirtualNetworkTokenAddress(token) {
      const virtualAddress = String(this.tokenAddressDrafts[token.id] || '').trim();
      if (!this.detailsNetwork || !token.id || !virtualAddress || this.dialogBusy) return;
      this.dialogBusy = true;
      try {
        const result = await api(`/api/virtual-networks/${this.detailsNetwork.id}/tokens/${token.id}/virtual-address`, {
          method: 'PUT',
          body: JSON.stringify({ virtualAddress })
        });
        await this.refreshVirtualNetworkDetails(this.detailsNetwork.id);
        this.setStatus(`Token ${token.tokenPreview} 的虚拟 IP 已改为 ${result.virtualAddress}，客户端将在配置刷新后自动切换`);
      } catch (error) {
        this.tokenAddressDrafts[token.id] = token.assignedVirtualAddress || '';
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    topologyNodeName(address) {
      const node = (this.virtualNetworkTopology?.nodes || []).find(item => item.virtualAddress === address);
      return node?.nodeName || node?.machineName || address;
    },

    topologyEdgeLatency(edge) {
      if (edge.rttMilliseconds == null) {
        if (this.language === 'en-US') return edge.connectionType === 'direct' ? 'Direct' : 'Relay';
        return edge.connectionType === 'direct' ? '直连' : '中继';
      }
      return `${Math.round(edge.rttMilliseconds)} ms`;
    },

    topologyEdgeLoss(edge) {
      return edge.lossPercent == null ? '—' : `${Number(edge.lossPercent).toFixed(1)}%`;
    },

    async banVirtualNetworkNode(networkId, virtualAddress) {
      this.dialogBusy = true;
      try {
        await api(`/api/virtual-networks/${networkId}/bans`, {
          method: 'POST',
          body: JSON.stringify({ virtualAddress, reason: 'Banned by administrator' })
        });
        await this.refreshVirtualNetworkDetails(networkId);
        this.setStatus(`节点 ${virtualAddress} 已封禁`);
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async unbanVirtualNetworkNode(networkId, virtualAddress) {
      this.dialogBusy = true;
      try {
        await api(`/api/virtual-networks/${networkId}/bans/${encodeURIComponent(virtualAddress)}`, { method: 'DELETE' });
        await this.refreshVirtualNetworkDetails(networkId);
        this.setStatus(`节点 ${virtualAddress} 已解封`);
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async createPortTunnel() {
      this.dialogBusy = true;
      try {
        await api('/api/port-tunnels', { method: 'POST', body: JSON.stringify(this.portTunnelForm) });
        this.portTunnelForm = defaultPortTunnelForm();
        this.activeDialog = null;
        await this.refreshAll();
        this.setStatus('内网穿透已创建');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async openTokenTab(kind, id) {
      try {
        this.selectedTokenKind = kind;
        this.selectedTokenResourceId = id || null;
        this.hidePlainToken();
        this.switchTab('tokens');
        this.ensureTokenTypeAllowed();
        await this.loadSelectedTokens();
      } catch (error) {
        this.setStatus(error.message, true);
      }
    },

    hidePlainToken() {
      this.plainToken = '';
    },

    async createToken() {
      const id = this.reconcileSelectedTokenResource();
      if (!id) {
        this.setStatus('请选择资源', true);
        return;
      }
      this.dialogBusy = true;
      try {
        const body = {
          tokenType: this.tokenForm.tokenType,
          validFrom: this.tokenForm.validFrom ? new Date(this.tokenForm.validFrom).toISOString() : null,
          validUntil: this.tokenForm.validUntil ? new Date(this.tokenForm.validUntil).toISOString() : null
        };
        const result = await api(this.tokenResourcePath(this.selectedTokenKind, id), {
          method: 'POST',
          body: JSON.stringify(body)
        });
        this.plainToken = result.plainTextToken;
        await this.loadSelectedTokens();
        this.setStatus('Token 已创建并保存');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async copyStoredToken(id) {
      try {
        const result = await api(`/api/access-tokens/${id}/value`);
        this.plainToken = result.plainTextToken;
        await this.$nextTick();
        await this.copyText(result.plainTextToken, 'Token 已复制');
      } catch (error) {
        this.setStatus(error.message, true);
      }
    },

    async copyText(value, successMessage) {
      if (!value) {
        this.setStatus('没有可复制的内容', true);
        return;
      }
      try {
        if (navigator.clipboard?.writeText) {
          await navigator.clipboard.writeText(value);
          this.setStatus(successMessage);
          return;
        }
      } catch {
      }

      const input = document.createElement('textarea');
      input.value = value;
      input.setAttribute('readonly', '');
      input.style.position = 'fixed';
      input.style.opacity = '0';
      document.body.appendChild(input);
      input.select();
      const copied = document.execCommand('copy');
      input.remove();
      if (!copied) {
        selectNodeText(this.$refs.plainTokenValue);
        throw new Error('浏览器拒绝自动复制，Token 已显示，请按 Ctrl+C 复制');
      }
      this.setStatus(successMessage);
    },

    async updateSettings() {
      this.dialogBusy = true;
      try {
        this.settings = await api('/api/settings', { method: 'PUT', body: JSON.stringify(this.settingsForm) });
        this.settingsForm = { ...this.settings };
        this.userForm = defaultUserForm(this.settings);
        await this.refreshAll();
        this.setStatus('系统设置已保存');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    },

    async changePassword() {
      this.dialogBusy = true;
      try {
        await api('/api/auth/change-password', { method: 'POST', body: JSON.stringify(this.passwordForm) });
        this.activeDialog = null;
        this.passwordForm = defaultPasswordForm();
        this.setStatus('密码已修改');
      } catch (error) {
        this.setStatus(error.message, true);
      } finally {
        this.dialogBusy = false;
      }
    }
  }
}).mount('#app');
