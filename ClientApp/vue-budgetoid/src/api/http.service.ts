import axios from 'axios';
import { Observable } from 'rxjs';

export { get, post };

function get<T = unknown>(url: string, params?: unknown): Observable<T> {
  return new Observable<T>(subscriber => {
    axios
      .get<T>(url, { params })
      .then(response => {
        subscriber.next(response.data);
        subscriber.complete();
      })
      .catch(error => subscriber.error(error));
  });
}

function post<T = unknown>(url: string, payload: unknown): Observable<T> {
  return new Observable<T>(subscriber => {
    axios
      .post(url, payload)
      .then(response => {
        subscriber.next(response.data);
        subscriber.complete();
      })
      .catch(error => subscriber.error(error));
  });
}
