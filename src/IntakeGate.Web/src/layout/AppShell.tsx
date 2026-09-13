import AccountCircleOutlined from '@mui/icons-material/AccountCircleOutlined';
import CheckCircleOutlineRounded from '@mui/icons-material/CheckCircleOutlineRounded';
import HelpOutlineRounded from '@mui/icons-material/HelpOutlineRounded';
import HomeOutlined from '@mui/icons-material/HomeOutlined';
import LogoutRounded from '@mui/icons-material/LogoutRounded';
import MenuRounded from '@mui/icons-material/MenuRounded';
import SettingsOutlined from '@mui/icons-material/SettingsOutlined';
import SearchRounded from '@mui/icons-material/SearchRounded';
import ViewListRounded from '@mui/icons-material/ViewListRounded';
import HealthAndSafetyOutlined from '@mui/icons-material/HealthAndSafetyOutlined';
import FactCheckOutlined from '@mui/icons-material/FactCheckOutlined';
import {
  AppBar,
  Box,
  Button,
  Chip,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Stack,
  Toolbar,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import { useState } from 'react';
import { Link as RouterLink, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useApplicationState } from '../state/ApplicationStateContext';

const drawerWidth = 272;

export function AppShell() {
  const theme = useTheme();
  const desktop = useMediaQuery(theme.breakpoints.up('md'));
  const [drawerOpen, setDrawerOpen] = useState(false);
  const { user, logout } = useAuth();
  const { setup } = useApplicationState();
  const location = useLocation();
  if (!user) return null;

  const admin = user.role === 'admin';
  const navigation = [
    { label: 'Home', to: '/', icon: <HomeOutlined />, visible: true },
    { label: 'Analyze Ticket', to: '/analyze', icon: <SearchRounded />, visible: admin && setup?.setupComplete === true },
    { label: 'Runs', to: '/runs', icon: <ViewListRounded />, visible: setup?.setupComplete === true },
    { label: 'System Health', to: '/system-health', icon: <HealthAndSafetyOutlined />, visible: true },
    { label: 'Audit', to: '/audit', icon: <FactCheckOutlined />, visible: true },
    { label: 'Setup', to: '/setup', icon: <SettingsOutlined />, visible: admin && setup?.setupComplete === false },
    { label: 'Profile / Configuration (Beta)', to: '/configuration', icon: <SettingsOutlined />, visible: admin && setup?.setupComplete === true },
    { label: 'Help', to: '/help', icon: <HelpOutlineRounded />, visible: true },
  ].filter((item) => item.visible);

  const drawer = (
    <Box sx={{ width: drawerWidth, height: '100%', display: 'flex', flexDirection: 'column', bgcolor: '#102a43', color: '#f7fafc' }}>
      <Toolbar sx={{ minHeight: 76, alignItems: 'center', px: 2.5 }}>
        <Box sx={{ width: 34, height: 34, borderRadius: 2, display: 'grid', placeItems: 'center', bgcolor: 'primary.light', color: 'primary.dark', fontWeight: 900 }} aria-hidden>IG</Box>
        <Box sx={{ ml: 1.5 }}><Typography sx={{ fontWeight: 800, lineHeight: 1.1 }}>Engineering</Typography><Typography sx={{ fontWeight: 800, lineHeight: 1.1 }}>Intake Gate</Typography></Box>
      </Toolbar>
      <Divider sx={{ borderColor: 'rgba(255,255,255,.12)' }} />
      <Box component="nav" aria-label="Primary navigation" sx={{ p: 1.5, overflowY: 'auto', flex: 1 }}>
        <List disablePadding>
          {navigation.map((item) => {
            const selected = item.to === '/' ? location.pathname === '/' || location.pathname === '/home' : location.pathname === item.to || location.pathname.startsWith(`${item.to}/`);
            return (
              <ListItem disablePadding key={item.label} sx={{ mb: 0.5 }}>
                <ListItemButton component={RouterLink} to={item.to} selected={selected} onClick={() => setDrawerOpen(false)} sx={{ borderRadius: 2, color: 'inherit', '&.Mui-selected': { bgcolor: 'rgba(216,238,244,.17)' }, '&:hover': { bgcolor: 'rgba(255,255,255,.09)' } }}>
                  <ListItemIcon sx={{ color: 'inherit', minWidth: 40 }}>{item.icon}</ListItemIcon><ListItemText primary={item.label} />
                </ListItemButton>
              </ListItem>
            );
          })}
        </List>
      </Box>
      <Divider sx={{ borderColor: 'rgba(255,255,255,.12)' }} />
      <Box sx={{ p: 2 }}><Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}><AccountCircleOutlined /><Box sx={{ minWidth: 0 }}><Typography variant="body2" noWrap sx={{ fontWeight: 700 }}>{user.displayName ?? user.username}</Typography><Typography variant="caption" sx={{ color: 'rgba(255,255,255,.67)', textTransform: 'capitalize' }}>{user.role}</Typography></Box></Stack></Box>
    </Box>
  );

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <AppBar position="fixed" color="inherit" elevation={0} sx={{ zIndex: theme.zIndex.drawer + 1, borderBottom: 1, borderColor: 'divider', ml: { md: `${drawerWidth}px` }, width: { md: `calc(100% - ${drawerWidth}px)` } }}>
        <Toolbar sx={{ minHeight: 76 }}>
          {!desktop ? <IconButton edge="start" aria-label="Open navigation" onClick={() => setDrawerOpen(true)} sx={{ mr: 1 }}><MenuRounded /></IconButton> : null}
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flex: 1 }}>
            <Chip icon={<CheckCircleOutlineRounded />} label="Controlled Dry Run" color="success" variant="outlined" sx={{ fontWeight: 700 }} />
            <Chip label={setup?.setupComplete ? 'Setup complete' : 'Setup incomplete'} color={setup?.setupComplete ? 'default' : 'warning'} size="small" />
          </Stack>
          <Tooltip title="Sign out"><Button color="inherit" startIcon={<LogoutRounded />} onClick={() => void logout()}><Box component="span" sx={{ display: { xs: 'none', sm: 'inline' } }}>Sign out</Box></Button></Tooltip>
        </Toolbar>
      </AppBar>
      <Box component="aside" sx={{ width: { md: drawerWidth }, flexShrink: { md: 0 } }}>
        {desktop ? <Drawer variant="permanent" open slotProps={{ paper: { sx: { width: drawerWidth, border: 0 } } }}>{drawer}</Drawer> : <Drawer variant="temporary" open={drawerOpen} onClose={() => setDrawerOpen(false)} slotProps={{ root: { keepMounted: true }, paper: { sx: { width: drawerWidth } } }}>{drawer}</Drawer>}
      </Box>
      <Box component="main" id="main-content" sx={{ flex: 1, minWidth: 0, pt: '76px' }}>
        <Box sx={{ px: { xs: 2, sm: 3, lg: 5 }, py: { xs: 3, lg: 5 }, maxWidth: 1320, mx: 'auto' }}><Outlet /></Box>
      </Box>
    </Box>
  );
}
