module Utilities exposing (..)

import Http exposing (Error)


errorToString : Error -> String
errorToString error =
    case error of
        Http.BadUrl url ->
            "Bad URL: " ++ url

        Http.Timeout ->
            "Request Timeout"

        Http.NetworkError ->
            "Network Error"

        Http.BadStatus status ->
            "Bad Status: " ++ String.fromInt status

        Http.BadBody body ->
            "Bad Body: " ++ body
