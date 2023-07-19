module Entities.Account exposing (Account, decoder)

import Json.Decode as Decode exposing (Decoder)


type alias Account =
    { currency : String
    , id : String
    , name : String
    , balance : Float
    }


decoder : Decoder Account
decoder =
    Decode.map4 Account
        (Decode.field "currency" Decode.string)
        (Decode.field "id" Decode.string)
        (Decode.field "name" Decode.string)
        (Decode.field "balance" Decode.float)
