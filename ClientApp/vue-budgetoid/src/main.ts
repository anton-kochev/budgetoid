import './assets/main.css';

import { createPinia } from 'pinia';
import { createApp } from 'vue';
import vue3GoogleLogin from 'vue3-google-login';

import App from './App.vue';
import router from './router';

// Vuetify
import { createVuetify } from 'vuetify';
import * as components from 'vuetify/components';
import * as directives from 'vuetify/directives';
import 'vuetify/styles';

const app = createApp(App);
const vuetify = createVuetify({
  components,
  directives,
});

app.use(vue3GoogleLogin, {
  clientId: 'YOUR_CLIENT_ID.apps.googleusercontent.com',
});
app.use(createPinia());
app.use(router);
app.use(vuetify);

app.mount('#app');
