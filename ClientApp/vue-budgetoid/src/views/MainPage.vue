<template>
  <div class="main">
    <nav>
      <router-link to="/accounts">All Accounts</router-link> |
      <router-link to="/about">About</router-link>
    </nav>
  </div>
</template>

<script lang="ts">
import { Options, Vue } from 'vue-class-component';

@Options({})
export default class MainPage extends Vue {}
</script>
