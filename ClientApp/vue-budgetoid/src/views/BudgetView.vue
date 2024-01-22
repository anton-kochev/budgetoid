<script setup lang="ts">
import type { Transaction } from '@/api/models/transaction.model';
import { fetchUserTransactions } from '@/api/transactions';
import { onMounted, ref, type Ref } from 'vue';

const transactions: Ref<Transaction[] | null> = ref(null);

onMounted(() => {
  fetchUserTransactions().subscribe(data => {
    transactions.value = data;
  });
});
</script>

<template>
  <h1>This is a budget view</h1>
  <p>Number of transactions: {{ transactions?.length }}</p>
  <div
    v-for="transaction in transactions"
    :key="transaction.id"
    class="transaction-box"
  >
    <p>{{ transaction.id }}</p>
    <p>{{ transaction.amount }}</p>
    <p>{{ transaction.comment }}</p>
    <p>{{ transaction.date }}</p>
  </div>
</template>

<style>
.transaction-box {
  border: 1px solid #eeeeee;
  padding: 1rem;
}
</style>
@/api/models/transaction.model
