<template>
  <div class="login">
    <h1 style="text-transform: uppercase">Budgetoid application</h1>
    <GoogleLogin :callback="handleCredentialResponse" />
  </div>
</template>

<script lang="ts">
import jwt_decode, { JwtPayload } from 'jwt-decode';
import { Options, Vue } from 'vue-class-component';
@Options({})
export default class LoginPage extends Vue {
  public handleCredentialResponse(response: { credential: string }) {
    const responsePayload = jwt_decode<JwtPayload>(response.credential);
    console.log(responsePayload);
  }
}
</script>
