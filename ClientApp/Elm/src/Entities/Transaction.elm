module Entities.Transaction exposing (..)

import Json.Decode as D


type alias Transaction =
    { id : String
    , accountId : String
    , amount : Float
    , date : String
    , payee : String
    , categoryId : String
    , comment : String
    , tags : List String
    }


decoder : D.Decoder Transaction
decoder =
    D.succeed Transaction
        |> decodeApply (D.field "id" D.string)
        |> decodeApply (D.field "accountId" D.string)
        |> decodeApply (D.field "amount" D.float)
        |> decodeApply (D.field "date" D.string)
        |> decodeApply (D.field "payee" D.string)
        |> decodeApply (D.field "categoryId" D.string)
        |> decodeApply (D.field "comment" D.string)
        |> decodeApply (D.field "tags" (D.list D.string))


decodeApply : D.Decoder a -> D.Decoder (a -> b) -> D.Decoder b
decodeApply value partial =
    D.andThen (\p -> D.map p value) partial
