<script setup lang="ts">
import { jwtDecode, type JwtPayload } from 'jwt-decode';
import { GoogleLogin } from 'vue3-google-login';

function handleCredentialResponse(response: { credential: string }) {
  const responsePayload = jwtDecode<JwtPayload>(response.credential);
  console.log(responsePayload);
}
</script>

<template>
  <div class="login">
    <h1>Budgetoid application</h1>
    <GoogleLogin :callback="handleCredentialResponse" />
  </div>
</template>

<style>
.login-box {
  display: grid;
  grid-template-columns: 1fr;
  grid-gap: 0.5rem;
}
</style>
