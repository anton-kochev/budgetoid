module Transaction.View exposing (transactionsView)

import Entities.Transaction exposing (Transaction)
import Html exposing (Html, div, text)
import Update exposing (Msg(..))


transactionsView : List Transaction -> Html Msg
transactionsView transactions =
    div [] (List.map transactionView transactions)


transactionView : Transaction -> Html Msg
transactionView t =
    div []
        [ div [] [ text t.id ]
        , div [] [ text t.categoryId ]
        , div [] [ text t.date ]
        , div [] [ text t.payee ]
        , div [] [ text t.comment ]
        , div [] [ text (String.fromFloat t.amount) ]
        ]
