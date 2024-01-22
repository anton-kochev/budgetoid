export interface Transaction {
  accountId: string;
  amount: number;
  categoryId: string;
  comment: string;
  date: Date;
  id: string;
  payee: string;
  tags: string[];
}
