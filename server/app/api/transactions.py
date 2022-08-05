from flask import abort, jsonify

from . import api

transactions_mock = [{"id": 1, "value": -10.3},
                     {"id": 2, "value": 1243.434123}]


@api.route('/transactions/', methods=['GET'])
def get_transactions():
    return jsonify(transactions_mock)


@api.route('/transactions/<int:id>', methods=['GET'])
def get_transaction(id):
    for transaction in transactions_mock:
        if transaction.get('id') == id:
            return jsonify(transaction)

    abort(404)
