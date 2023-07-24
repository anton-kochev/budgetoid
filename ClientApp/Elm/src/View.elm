module View exposing (view)

import Account.View exposing (accountsView)
import Html exposing (Html, div, li, nav, section, text, ul)
import Maybe exposing (withDefault)
import Model exposing (Model)
import Transaction.View exposing (transactionsView)
import Update exposing (Msg(..))


view : Model -> Html Msg
view model =
    div []
        [ nav []
            [ section []
                [ ul []
                    [ li [] [ text "Budget" ]
                    , li [] [ text "Report" ]
                    , li [] [ text "All accounts" ]
                    ]
                ]
            , section []
                [ text "Accounts"
                , fallback accountsView model.accounts "Loading..."
                ]
            ]
        , section []
            [ text ("Selected Account: " ++ withDefault "empty" (Maybe.map .id model.selectedAccount))
            , fallback transactionsView model.transactions "Loading..."
            ]
        ]


fallback : (List a -> Html msg) -> Maybe (List a) -> String -> Html msg
fallback v x fallbackText =
    if x == Nothing then
        div [] [ text fallbackText ]

    else
        v (Maybe.withDefault [] x)
