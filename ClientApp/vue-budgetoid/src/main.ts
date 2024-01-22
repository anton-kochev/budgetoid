import './assets/main.css';

import { createPinia } from 'pinia';
import { createApp } from 'vue';
import vue3GoogleLogin from 'vue3-google-login';

import App from './App.vue';
import router from './router';

const app = createApp(App);

app.use(vue3GoogleLogin, {
  clientId: '',
});
app.use(createPinia());
app.use(router);

app.mount('#app');
