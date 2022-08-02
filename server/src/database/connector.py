from pprint import pprint

from pymongo import MongoClient

# connect to MongoDB
client = MongoClient('mongodb://docker:mongopw@localhost:55000')
# db = client.admin
# Issue the serverStatus command and print the results
# serverStatusResult = db.command("serverStatus")
# pprint(serverStatusResult)

db = client.budgetoid
db.transactions.insert_one(
    {'amount': 12, 'client_id': 123, 'category': 'default'})

db.transactions.delete_many({})
