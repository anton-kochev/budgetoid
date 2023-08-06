<template>
  <div class="home">
    <h1>Welcome to Budgetoid</h1>
    <h3>The free and open-source budget management tool</h3>
  </div>
</template>

<script lang="ts">
import HelloWorld from '@/components/HelloWorld.vue'; // @ is an alias to /src
import { Options, Vue } from 'vue-class-component';

@Options({
  components: {
    HelloWorld,
  },
})
export default class WelcomePage extends Vue {}
</script>
