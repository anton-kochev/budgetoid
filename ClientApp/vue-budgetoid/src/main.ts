import { createApp } from 'vue';
import vue3GoogleLogin from 'vue3-google-login';
import App from './App.vue';
import router from './router';
import store from './store';

createApp(App)
  .use(vue3GoogleLogin, {
    clientId: '',
  })
  .use(store)
  .use(router)
  .mount('#app');
