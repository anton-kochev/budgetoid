import { Injectable } from '@angular/core';
import { DefaultOAuthInterceptor } from 'angular-oauth2-oidc';

@Injectable()
export class AuthInterceptor extends DefaultOAuthInterceptor {}
